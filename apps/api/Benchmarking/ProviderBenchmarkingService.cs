using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Benchmarking;

public sealed class ProviderBenchmarkService(TaslimDbContext db, TimeProvider timeProvider) : IProviderBenchmarkService
{
    public async Task<ProviderBenchmarkRun> GetOrCreateRunAsync(
        ProviderBenchmarkRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var runKey = ProviderBenchmarkValidation.Required(request.RunKey, nameof(request.RunKey), 180);
        var definition = ProviderBenchmarkValidation.Required(request.BenchmarkDefinition, nameof(request.BenchmarkDefinition), 160);
        var existing = await db.ProviderBenchmarkRuns.SingleOrDefaultAsync(item => item.RunKey == runKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.BenchmarkDefinition, definition, StringComparison.Ordinal))
                throw new InvalidOperationException("The benchmark run key is already associated with a different definition.");

            return existing;
        }

        var now = UtcNow();
        var run = new ProviderBenchmarkRun
        {
            Id = Guid.NewGuid(),
            RunKey = runKey,
            BenchmarkDefinition = definition,
            FixtureManifestHash = ProviderBenchmarkValidation.Optional(request.FixtureManifestHash, nameof(request.FixtureManifestHash), 128),
            HarnessVersion = ProviderBenchmarkValidation.Optional(request.HarnessVersion, nameof(request.HarnessVersion), 80),
            EnvironmentFingerprint = ProviderBenchmarkValidation.Optional(request.EnvironmentFingerprint, nameof(request.EnvironmentFingerprint), 240),
            ProvenanceJson = ProviderBenchmarkValidation.Json(request.ProvenanceJson, nameof(request.ProvenanceJson), 20_000),
            Status = ProviderBenchmarkRunStatus.Running,
            CreatedAt = now,
            StartedAt = now,
        };

        db.ProviderBenchmarkRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException)
        {
            // A concurrent harness instance may have won the unique RunKey race.
            db.Entry(run).State = EntityState.Detached;
            existing = await db.ProviderBenchmarkRuns.SingleOrDefaultAsync(item => item.RunKey == runKey, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.BenchmarkDefinition, definition, StringComparison.Ordinal))
                    throw new InvalidOperationException("The benchmark run key is already associated with a different definition.");
                return existing;
            }

            throw;
        }
    }

    public async Task<ProviderBenchmarkScenario> UpsertScenarioAsync(
        Guid runId,
        ProviderBenchmarkScenarioInput input,
        CancellationToken cancellationToken = default)
    {
        var run = await GetWritableRunAsync(runId, cancellationToken);
        var scenarioKey = ProviderBenchmarkValidation.Required(input.ScenarioKey, nameof(input.ScenarioKey), 160);
        var scenario = await db.ProviderBenchmarkScenarios
            .SingleOrDefaultAsync(item => item.ProviderBenchmarkRunId == runId && item.ScenarioKey == scenarioKey, cancellationToken);
        var now = UtcNow();

        if (scenario is null)
        {
            scenario = new ProviderBenchmarkScenario
            {
                Id = Guid.NewGuid(),
                ProviderBenchmarkRunId = runId,
                ScenarioKey = scenarioKey,
                CreatedAt = now,
            };
            db.ProviderBenchmarkScenarios.Add(scenario);
        }

        ApplyScenario(scenario, input);
        scenario.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return scenario;
        }
        catch (DbUpdateException) when (scenario.Id != Guid.Empty)
        {
            // Re-read a concurrently inserted scenario and apply the same payload.
            db.Entry(scenario).State = EntityState.Detached;
            var concurrent = await db.ProviderBenchmarkScenarios
                .SingleOrDefaultAsync(item => item.ProviderBenchmarkRunId == runId && item.ScenarioKey == scenarioKey, cancellationToken);
            if (concurrent is null) throw;
            ApplyScenario(concurrent, input);
            concurrent.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return concurrent;
        }
    }

    public async Task<ProviderBenchmarkMeasurement> UpsertMeasurementAsync(
        Guid runId,
        Guid scenarioId,
        ProviderBenchmarkMeasurementInput input,
        CancellationToken cancellationToken = default)
    {
        _ = await GetWritableRunAsync(runId, cancellationToken);
        var scenarioExists = await db.ProviderBenchmarkScenarios
            .AnyAsync(item => item.Id == scenarioId && item.ProviderBenchmarkRunId == runId, cancellationToken);
        if (!scenarioExists) throw new KeyNotFoundException("The benchmark scenario was not found for this run.");

        var measurementKey = ProviderBenchmarkValidation.Required(input.MeasurementKey, nameof(input.MeasurementKey), 180);
        var providerKey = ProviderBenchmarkValidation.Required(input.ProviderKey, nameof(input.ProviderKey), 80);
        var modelKey = ProviderBenchmarkValidation.Required(input.ModelKey, nameof(input.ModelKey), 160);
        ValidateMeasurement(input);

        var measurement = await db.ProviderBenchmarkMeasurements
            .SingleOrDefaultAsync(item => item.ProviderBenchmarkRunId == runId && item.MeasurementKey == measurementKey, cancellationToken);
        var now = UtcNow();
        if (measurement is null)
        {
            measurement = new ProviderBenchmarkMeasurement
            {
                Id = Guid.NewGuid(),
                ProviderBenchmarkRunId = runId,
                ProviderBenchmarkScenarioId = scenarioId,
                MeasurementKey = measurementKey,
                StartedAt = Utc(input.StartedAt) ?? now,
            };
            db.ProviderBenchmarkMeasurements.Add(measurement);
        }
        else if (measurement.ProviderBenchmarkScenarioId != scenarioId
                 || !string.Equals(measurement.ProviderKey, providerKey, StringComparison.Ordinal)
                 || !string.Equals(measurement.ModelKey, modelKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The measurement key is already associated with a different benchmark observation.");
        }

        ApplyMeasurement(measurement, input, providerKey, modelKey, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return measurement;
        }
        catch (DbUpdateException) when (measurement.Id != Guid.Empty)
        {
            db.Entry(measurement).State = EntityState.Detached;
            var concurrent = await db.ProviderBenchmarkMeasurements
                .SingleOrDefaultAsync(item => item.ProviderBenchmarkRunId == runId && item.MeasurementKey == measurementKey, cancellationToken);
            if (concurrent is null) throw;
            if (concurrent.ProviderBenchmarkScenarioId != scenarioId
                || !string.Equals(concurrent.ProviderKey, providerKey, StringComparison.Ordinal)
                || !string.Equals(concurrent.ModelKey, modelKey, StringComparison.Ordinal))
                throw new InvalidOperationException("The measurement key is already associated with a different benchmark observation.");
            ApplyMeasurement(concurrent, input, providerKey, modelKey, now);
            await db.SaveChangesAsync(cancellationToken);
            return concurrent;
        }
    }

    public async Task<ProviderBenchmarkEvidence> UpsertEvidenceAsync(
        Guid runId,
        Guid measurementId,
        ProviderBenchmarkEvidenceInput input,
        CancellationToken cancellationToken = default)
    {
        _ = await GetWritableRunAsync(runId, cancellationToken);
        var measurementExists = await db.ProviderBenchmarkMeasurements
            .AnyAsync(item => item.Id == measurementId && item.ProviderBenchmarkRunId == runId, cancellationToken);
        if (!measurementExists) throw new KeyNotFoundException("The benchmark measurement was not found for this run.");

        var evidenceKey = ProviderBenchmarkValidation.Required(input.EvidenceKey, nameof(input.EvidenceKey), 180);
        var evidenceType = ProviderBenchmarkValidation.Required(input.EvidenceType, nameof(input.EvidenceType), 60);
        var source = ProviderBenchmarkValidation.Required(input.Source, nameof(input.Source), 160);
        var payload = ProviderBenchmarkValidation.Json(input.PayloadJson, nameof(input.PayloadJson), 40_000);
        var provenance = ProviderBenchmarkValidation.Json(input.ProvenanceJson, nameof(input.ProvenanceJson), 20_000);
        var evidence = await db.ProviderBenchmarkEvidence
            .SingleOrDefaultAsync(item => item.ProviderBenchmarkMeasurementId == measurementId && item.EvidenceKey == evidenceKey, cancellationToken);
        var now = UtcNow();

        if (evidence is null)
        {
            evidence = new ProviderBenchmarkEvidence
            {
                Id = Guid.NewGuid(),
                ProviderBenchmarkMeasurementId = measurementId,
                EvidenceKey = evidenceKey,
                CreatedAt = now,
            };
            db.ProviderBenchmarkEvidence.Add(evidence);
        }

        evidence.EvidenceType = evidenceType;
        evidence.Source = source;
        evidence.Reference = ProviderBenchmarkValidation.Optional(input.Reference, nameof(input.Reference), 600);
        evidence.ContentHash = ProviderBenchmarkValidation.Optional(input.ContentHash, nameof(input.ContentHash), 128);
        evidence.PayloadJson = payload;
        evidence.ProvenanceJson = provenance;
        evidence.CapturedAt = Utc(input.CapturedAt) ?? now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return evidence;
        }
        catch (DbUpdateException) when (evidence.Id != Guid.Empty)
        {
            db.Entry(evidence).State = EntityState.Detached;
            var concurrent = await db.ProviderBenchmarkEvidence
                .SingleOrDefaultAsync(item => item.ProviderBenchmarkMeasurementId == measurementId && item.EvidenceKey == evidenceKey, cancellationToken);
            if (concurrent is null) throw;
            concurrent.EvidenceType = evidenceType;
            concurrent.Source = source;
            concurrent.Reference = evidence.Reference;
            concurrent.ContentHash = evidence.ContentHash;
            concurrent.PayloadJson = payload;
            concurrent.ProvenanceJson = provenance;
            concurrent.CapturedAt = evidence.CapturedAt;
            await db.SaveChangesAsync(cancellationToken);
            return concurrent;
        }
    }

    public async Task<ProviderBenchmarkRun> CompleteRunAsync(
        Guid runId,
        ProviderBenchmarkRunCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (completion.Status == ProviderBenchmarkRunStatus.Running)
            throw new ArgumentException("A run completion status must be terminal.", nameof(completion));

        var run = await db.ProviderBenchmarkRuns.SingleOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("The benchmark run was not found.");
        if (run.Status != ProviderBenchmarkRunStatus.Running)
        {
            if (run.Status != completion.Status)
                throw new InvalidOperationException("The benchmark run has already reached a different terminal state.");
            return run;
        }

        run.Status = completion.Status;
        run.CompletionCode = ProviderBenchmarkValidation.Optional(completion.CompletionCode, nameof(completion.CompletionCode), 100);
        run.CompletedAt = Utc(completion.CompletedAt) ?? UtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public Task<ProviderBenchmarkRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        db.ProviderBenchmarkRuns
            .AsNoTracking()
            .Include(item => item.Scenarios)
                .ThenInclude(item => item.Measurements)
                    .ThenInclude(item => item.Evidence)
            .SingleOrDefaultAsync(item => item.Id == runId, cancellationToken);

    public async Task<IReadOnlyList<ProviderBenchmarkAggregate>> GetAggregatesAsync(
        ProviderBenchmarkAggregateQuery query,
        CancellationToken cancellationToken = default)
    {
        var measurements = db.ProviderBenchmarkMeasurements
            .AsNoTracking()
            .Where(item => query.RunId == null || item.ProviderBenchmarkRunId == query.RunId)
            .Where(item => query.ScenarioKey == null || item.Scenario.ScenarioKey == query.ScenarioKey)
            .Where(item => query.CharacteristicsHash == null || item.Scenario.CharacteristicsHash == query.CharacteristicsHash)
            .Where(item => query.ProviderKey == null || item.ProviderKey == query.ProviderKey)
            .Where(item => query.ModelKey == null || item.ModelKey == query.ModelKey)
            .Select(item => new
            {
                item.Scenario.ScenarioKey,
                item.Scenario.CharacteristicsHash,
                item.Scenario.ShotType,
                item.ProviderKey,
                item.ModelKey,
                item.Status,
                item.TotalLatencyMs,
                item.QualityScore,
                item.ContinuityScore,
                item.ActualCostUsd,
            });

        var rows = await measurements.ToListAsync(cancellationToken);
        return rows
            .GroupBy(item => new { item.ScenarioKey, item.CharacteristicsHash, item.ShotType, item.ProviderKey, item.ModelKey })
            .OrderBy(group => group.Key.ScenarioKey, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ProviderKey, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ModelKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var latency = group.Where(item => item.TotalLatencyMs.HasValue).Select(item => (double)item.TotalLatencyMs!.Value).OrderBy(item => item).ToArray();
                var successCount = group.Count(item => item.Status == ProviderBenchmarkMeasurementStatus.Succeeded);
                return new ProviderBenchmarkAggregate(
                    group.Key.ScenarioKey,
                    group.Key.CharacteristicsHash,
                    group.Key.ShotType,
                    group.Key.ProviderKey,
                    group.Key.ModelKey,
                    group.Count(),
                    successCount,
                    decimal.Round((decimal)successCount / group.Count(), 6, MidpointRounding.AwayFromZero),
                    latency.Length == 0 ? null : latency.Average(),
                    Median(latency),
                    Average(group.Select(item => item.QualityScore)),
                    Average(group.Select(item => item.ContinuityScore)),
                    Average(group.Select(item => item.ActualCostUsd)));
            })
            .ToArray();
    }

    private async Task<ProviderBenchmarkRun> GetWritableRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.ProviderBenchmarkRuns.SingleOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("The benchmark run was not found.");
        if (run.Status != ProviderBenchmarkRunStatus.Running)
            throw new InvalidOperationException("The benchmark run is no longer writable.");
        return run;
    }

    private static void ApplyScenario(ProviderBenchmarkScenario scenario, ProviderBenchmarkScenarioInput input)
    {
        scenario.FixtureRevision = ProviderBenchmarkValidation.Required(input.FixtureRevision, nameof(input.FixtureRevision), 80);
        scenario.CharacteristicsHash = ProviderBenchmarkValidation.Required(input.CharacteristicsHash, nameof(input.CharacteristicsHash), 128);
        scenario.ShotType = ProviderBenchmarkValidation.Required(input.ShotType, nameof(input.ShotType), 80);
        scenario.CameraMotion = ProviderBenchmarkValidation.Optional(input.CameraMotion, nameof(input.CameraMotion), 120);
        scenario.AspectRatio = ProviderBenchmarkValidation.Optional(input.AspectRatio, nameof(input.AspectRatio), 30);
        scenario.TargetResolution = ProviderBenchmarkValidation.Optional(input.TargetResolution, nameof(input.TargetResolution), 40);
        ProviderBenchmarkValidation.NonNegative(input.DurationSeconds, nameof(input.DurationSeconds));
        ProviderBenchmarkValidation.NonNegative(input.SubjectCount, nameof(input.SubjectCount));
        ProviderBenchmarkValidation.NonNegative(input.ReferenceFrameCount, nameof(input.ReferenceFrameCount));
        scenario.DurationSeconds = input.DurationSeconds;
        scenario.SubjectCount = input.SubjectCount;
        scenario.ReferenceFrameCount = input.ReferenceFrameCount;
        scenario.RequiresCharacterContinuity = input.RequiresCharacterContinuity;
        scenario.HasDialogue = input.HasDialogue;
        scenario.HasNativeAudio = input.HasNativeAudio;
        scenario.CharacteristicsJson = ProviderBenchmarkValidation.Json(input.CharacteristicsJson, nameof(input.CharacteristicsJson), 40_000);
        scenario.FixtureProvenanceJson = ProviderBenchmarkValidation.Json(input.FixtureProvenanceJson, nameof(input.FixtureProvenanceJson), 20_000);
    }

    private static void ApplyMeasurement(ProviderBenchmarkMeasurement measurement, ProviderBenchmarkMeasurementInput input, string providerKey, string modelKey, DateTime now)
    {
        measurement.ProviderKey = providerKey;
        measurement.ModelKey = modelKey;
        measurement.Status = input.Status;
        measurement.AttemptNumber = input.AttemptNumber;
        measurement.RetryCount = input.RetryCount;
        measurement.QueueLatencyMs = input.QueueLatencyMs;
        measurement.TimeToFirstFrameMs = input.TimeToFirstFrameMs;
        measurement.GenerationLatencyMs = input.GenerationLatencyMs;
        measurement.TotalLatencyMs = input.TotalLatencyMs;
        measurement.OutputDurationMs = input.OutputDurationMs;
        measurement.OutputWidth = input.OutputWidth;
        measurement.OutputHeight = input.OutputHeight;
        measurement.OutputBytes = input.OutputBytes;
        measurement.EstimatedCostUsd = input.EstimatedCostUsd;
        measurement.ActualCostUsd = input.ActualCostUsd;
        measurement.ActualCostKnown = input.ActualCostKnown;
        measurement.QualityScore = input.QualityScore;
        measurement.ContinuityScore = input.ContinuityScore;
        measurement.TemporalStabilityScore = input.TemporalStabilityScore;
        measurement.PromptAdherenceScore = input.PromptAdherenceScore;
        measurement.Currency = ProviderBenchmarkValidation.Required(input.Currency, nameof(input.Currency), 3).ToUpperInvariant();
        measurement.ErrorCode = ProviderBenchmarkValidation.Optional(input.ErrorCode, nameof(input.ErrorCode), 100);
        measurement.MetricsJson = ProviderBenchmarkValidation.Json(input.MetricsJson, nameof(input.MetricsJson), 40_000);
        measurement.ProvenanceJson = ProviderBenchmarkValidation.Json(input.ProvenanceJson, nameof(input.ProvenanceJson), 20_000);
        measurement.StartedAt = Utc(input.StartedAt) ?? measurement.StartedAt;
        measurement.CompletedAt = Utc(input.CompletedAt);
        measurement.RecordedAt = now;
    }

    private static void ValidateMeasurement(ProviderBenchmarkMeasurementInput input)
    {
        if (input.AttemptNumber < 1) throw new ArgumentOutOfRangeException(nameof(input.AttemptNumber));
        if (input.RetryCount < 0) throw new ArgumentOutOfRangeException(nameof(input.RetryCount));
        ProviderBenchmarkValidation.NonNegative(input.QueueLatencyMs, nameof(input.QueueLatencyMs));
        ProviderBenchmarkValidation.NonNegative(input.TimeToFirstFrameMs, nameof(input.TimeToFirstFrameMs));
        ProviderBenchmarkValidation.NonNegative(input.GenerationLatencyMs, nameof(input.GenerationLatencyMs));
        ProviderBenchmarkValidation.NonNegative(input.TotalLatencyMs, nameof(input.TotalLatencyMs));
        ProviderBenchmarkValidation.NonNegative(input.OutputDurationMs, nameof(input.OutputDurationMs));
        ProviderBenchmarkValidation.NonNegative(input.OutputWidth, nameof(input.OutputWidth));
        ProviderBenchmarkValidation.NonNegative(input.OutputHeight, nameof(input.OutputHeight));
        ProviderBenchmarkValidation.NonNegative(input.OutputBytes, nameof(input.OutputBytes));
        ProviderBenchmarkValidation.NonNegative(input.EstimatedCostUsd, nameof(input.EstimatedCostUsd));
        ProviderBenchmarkValidation.NonNegative(input.ActualCostUsd, nameof(input.ActualCostUsd));
        ProviderBenchmarkValidation.Score(input.QualityScore, nameof(input.QualityScore));
        ProviderBenchmarkValidation.Score(input.ContinuityScore, nameof(input.ContinuityScore));
        ProviderBenchmarkValidation.Score(input.TemporalStabilityScore, nameof(input.TemporalStabilityScore));
        ProviderBenchmarkValidation.Score(input.PromptAdherenceScore, nameof(input.PromptAdherenceScore));
    }

    private DateTime UtcNow() => Utc(timeProvider.GetUtcNow().UtcDateTime)!.Value;

    private static DateTime? Utc(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static double? Median(double[] values)
    {
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2d : values[middle];
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var materialized = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : decimal.Round(materialized.Average(), 6, MidpointRounding.AwayFromZero);
    }
}

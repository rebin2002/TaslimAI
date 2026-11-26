using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Benchmarking;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderBenchmarkingTests
{
    [Fact]
    public async Task Run_scenario_measurement_and_evidence_are_rerunnable_and_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = new ProviderBenchmarkService(db, TimeProvider.System);

        var request = new ProviderBenchmarkRunRequest(
            "wave3-shot-fixtures-v1-run-001",
            "movie-shot-provider-benchmark",
            FixtureManifestHash: "fixture-manifest-hash",
            HarnessVersion: "harness-v1",
            EnvironmentFingerprint: "test-environment",
            ProvenanceJson: "{\"source\":\"fixture-suite\"}");
        var firstRun = await service.GetOrCreateRunAsync(request);
        var replayedRun = await service.GetOrCreateRunAsync(request);

        Assert.Equal(firstRun.Id, replayedRun.Id);
        Assert.Equal(1, await db.ProviderBenchmarkRuns.CountAsync());

        var scenarioInput = new ProviderBenchmarkScenarioInput(
            "close-up-dialogue-001",
            "fixture-v1",
            "character-close-up-hash",
            "close-up",
            CameraMotion: "static",
            AspectRatio: "16:9",
            TargetResolution: "1920x1080",
            DurationSeconds: 5,
            SubjectCount: 1,
            ReferenceFrameCount: 2,
            RequiresCharacterContinuity: true,
            HasDialogue: true,
            HasNativeAudio: false,
            CharacteristicsJson: "{\"lighting\":\"low-key\"}",
            FixtureProvenanceJson: "{\"catalog\":\"internal\"}");
        var scenario = await service.UpsertScenarioAsync(firstRun.Id, scenarioInput);
        var scenarioReplay = await service.UpsertScenarioAsync(firstRun.Id, scenarioInput with { CameraMotion = "locked-camera" });

        Assert.Equal(scenario.Id, scenarioReplay.Id);
        Assert.Equal("locked-camera", scenarioReplay.CameraMotion);
        Assert.Equal(1, await db.ProviderBenchmarkScenarios.CountAsync());

        var measurementInput = new ProviderBenchmarkMeasurementInput(
            "close-up-dialogue-001-route-a-attempt-1",
            "provider-route-a",
            "model-route-a-v1",
            ProviderBenchmarkMeasurementStatus.Succeeded,
            AttemptNumber: 1,
            TotalLatencyMs: 1_200,
            TimeToFirstFrameMs: 400,
            OutputDurationMs: 5_000,
            OutputWidth: 1_920,
            OutputHeight: 1_080,
            OutputBytes: 128_000,
            EstimatedCostUsd: 0.12m,
            ActualCostUsd: 0.12m,
            ActualCostKnown: true,
            QualityScore: 82.5m,
            ContinuityScore: 78m,
            MetricsJson: "{\"flickerRate\":0.02}",
            ProvenanceJson: "{\"capture\":\"deterministic\"}");
        var measurement = await service.UpsertMeasurementAsync(firstRun.Id, scenario.Id, measurementInput);
        var measurementReplay = await service.UpsertMeasurementAsync(
            firstRun.Id,
            scenario.Id,
            measurementInput with { QualityScore = 84m, TotalLatencyMs = 1_250 });

        Assert.Equal(measurement.Id, measurementReplay.Id);
        Assert.Equal(84m, measurementReplay.QualityScore);
        Assert.Equal(1, await db.ProviderBenchmarkMeasurements.CountAsync());

        var evidenceInput = new ProviderBenchmarkEvidenceInput(
            "output-hash",
            "output_hash",
            "automated-capture",
            Reference: "benchmark-artifact-001",
            ContentHash: "sha256:abc",
            PayloadJson: "{\"sha256\":\"abc\"}",
            ProvenanceJson: "{\"capturedBy\":\"harness\"}");
        var evidence = await service.UpsertEvidenceAsync(firstRun.Id, measurement.Id, evidenceInput);
        var evidenceReplay = await service.UpsertEvidenceAsync(firstRun.Id, measurement.Id, evidenceInput with { Reference = "benchmark-artifact-001-replayed" });

        Assert.Equal(evidence.Id, evidenceReplay.Id);
        Assert.Equal("benchmark-artifact-001-replayed", evidenceReplay.Reference);
        Assert.Equal(1, await db.ProviderBenchmarkEvidence.CountAsync());

        var aggregates = await service.GetAggregatesAsync(new ProviderBenchmarkAggregateQuery(RunId: firstRun.Id));
        var aggregate = Assert.Single(aggregates);
        Assert.Equal("provider-route-a", aggregate.ProviderKey);
        Assert.Equal("model-route-a-v1", aggregate.ModelKey);
        Assert.Equal(1, aggregate.SampleCount);
        Assert.Equal(1, aggregate.SuccessCount);
        Assert.Equal(1m, aggregate.SuccessRate);
        Assert.Equal(1_250d, aggregate.AverageTotalLatencyMs);
        Assert.Equal(84m, aggregate.AverageQualityScore);

        var completed = await service.CompleteRunAsync(firstRun.Id, new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "observations-recorded"));
        var completedReplay = await service.CompleteRunAsync(firstRun.Id, new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "ignored-on-replay"));
        Assert.Equal(completed.Id, completedReplay.Id);
        Assert.Equal("observations-recorded", completedReplay.CompletionCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertScenarioAsync(firstRun.Id, scenarioInput));
    }

    [Fact]
    public async Task Aggregation_preserves_each_route_and_does_not_choose_a_permanent_winner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = new ProviderBenchmarkService(db, TimeProvider.System);
        var run = await service.GetOrCreateRunAsync(new ProviderBenchmarkRunRequest("comparison-run-001", "movie-shot-provider-benchmark"));
        var scenario = await service.UpsertScenarioAsync(run.Id, new ProviderBenchmarkScenarioInput("tracking-001", "fixture-v1", "tracking-hash", "tracking", CameraMotion: "handheld", DurationSeconds: 10));

        await service.UpsertMeasurementAsync(run.Id, scenario.Id, new ProviderBenchmarkMeasurementInput("route-a-attempt-1", "provider-route-a", "model-a", ProviderBenchmarkMeasurementStatus.Succeeded, TotalLatencyMs: 1_000, QualityScore: 70m));
        await service.UpsertMeasurementAsync(run.Id, scenario.Id, new ProviderBenchmarkMeasurementInput("route-b-attempt-1", "provider-route-b", "model-b", ProviderBenchmarkMeasurementStatus.Failed, TotalLatencyMs: 2_000, ErrorCode: "temporary-failure"));

        var aggregates = await service.GetAggregatesAsync(new ProviderBenchmarkAggregateQuery(CharacteristicsHash: "tracking-hash"));

        Assert.Equal(2, aggregates.Count);
        Assert.Equal(["provider-route-a", "provider-route-b"], aggregates.Select(item => item.ProviderKey).ToArray());
        Assert.Equal(1m, aggregates[0].SuccessRate);
        Assert.Equal(0m, aggregates[1].SuccessRate);
        Assert.Equal(1_000d, aggregates[0].P50TotalLatencyMs);
        Assert.Equal(2_000d, aggregates[1].P50TotalLatencyMs);
    }

    [Fact]
    public async Task Invalid_json_scores_and_negative_metrics_are_rejected_before_persistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = new ProviderBenchmarkService(db, TimeProvider.System);
        var run = await service.GetOrCreateRunAsync(new ProviderBenchmarkRunRequest("validation-run-001", "movie-shot-provider-benchmark"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertScenarioAsync(run.Id, new ProviderBenchmarkScenarioInput("bad-json", "v1", "hash", "wide", CharacteristicsJson: "not-json")));
        var scenario = await service.UpsertScenarioAsync(run.Id, new ProviderBenchmarkScenarioInput("valid", "v1", "hash", "wide"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpsertMeasurementAsync(run.Id, scenario.Id, new ProviderBenchmarkMeasurementInput("negative", "route", "model", ProviderBenchmarkMeasurementStatus.Failed, TotalLatencyMs: -1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpsertMeasurementAsync(run.Id, scenario.Id, new ProviderBenchmarkMeasurementInput("bad-score", "route", "model", ProviderBenchmarkMeasurementStatus.Succeeded, QualityScore: 101m)));

        Assert.Equal(0, await db.ProviderBenchmarkMeasurements.CountAsync());
    }

    [Fact]
    public async Task Same_terminal_replay_refreshes_persisted_metadata_before_skipping_validation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var service = new ProviderBenchmarkService(db, TimeProvider.System);
        var run = await service.GetOrCreateRunAsync(new ProviderBenchmarkRunRequest("persisted-replay-001", "provider-benchmark-replay"));
        var requestedAt = new DateTime(2026, 10, 26, 2, 15, 0, DateTimeKind.Utc).AddTicks(1);

        await service.CompleteRunAsync(run.Id,
            new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "initial-completion", requestedAt));

        var persistedAt = requestedAt.AddSeconds(1);
        await using (var writer = CreateDb(connection))
        {
            var persisted = await writer.ProviderBenchmarkRuns.SingleAsync(item => item.Id == run.Id);
            persisted.CompletionCode = "database-completion";
            persisted.CompletedAt = persistedAt;
            await writer.SaveChangesAsync();
        }

        var replay = await service.CompleteRunAsync(run.Id,
            new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, new string('x', 101)));

        Assert.Equal("database-completion", replay.CompletionCode);
        Assert.Equal(persistedAt, replay.CompletedAt);
        Assert.Same(db.ProviderBenchmarkRuns.Local.Single(item => item.Id == run.Id), replay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteRunAsync(run.Id,
            new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Cancelled, new string('x', 101))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CompleteRunAsync(Guid.NewGuid(),
            new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, new string('x', 101))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteRunAsync(Guid.NewGuid(),
            new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Running, new string('x', 101))));
    }

    [DisposablePostgresFact]
    public async Task Completion_and_immediate_replay_return_postgres_persisted_timestamp_precision()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"benchmark_replay_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scopedBuilder.ConnectionString).Options;
            await using var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var service = new ProviderBenchmarkService(db, TimeProvider.System);
            var run = await service.GetOrCreateRunAsync(new ProviderBenchmarkRunRequest(
                $"postgres-precision-{Guid.NewGuid():N}", "provider-benchmark-replay"));
            var requestedAt = new DateTime(2026, 10, 26, 2, 30, 0, DateTimeKind.Utc).AddTicks(1);

            var completed = await service.CompleteRunAsync(run.Id,
                new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, "postgres-precision", requestedAt));
            var persisted = await db.ProviderBenchmarkRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id);

            Assert.NotNull(persisted.CompletedAt);
            Assert.NotEqual(requestedAt, persisted.CompletedAt.Value);
            Assert.Equal(persisted.CompletedAt, completed.CompletedAt);

            var replay = await service.CompleteRunAsync(run.Id,
                new ProviderBenchmarkRunCompletion(ProviderBenchmarkRunStatus.Completed, new string('x', 101)));
            Assert.Equal(persisted.CompletionCode, replay.CompletionCode);
            Assert.Equal(persisted.CompletedAt, replay.CompletedAt);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var drop = cleanup.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
}

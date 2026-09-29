using System.Text.Json;

namespace Taslim.Api.Benchmarking;

public enum ProviderBenchmarkRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

public enum ProviderBenchmarkMeasurementStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

/// <summary>
/// A durable, provider-neutral benchmark run. Provider and model identifiers are
/// intentionally kept on internal measurement records and are not user-facing data.
/// </summary>
public sealed class ProviderBenchmarkRun
{
    public Guid Id { get; set; }
    public string RunKey { get; set; } = string.Empty;
    public string BenchmarkDefinition { get; set; } = string.Empty;
    public string? FixtureManifestHash { get; set; }
    public string? HarnessVersion { get; set; }
    public string? EnvironmentFingerprint { get; set; }
    public string? ProvenanceJson { get; set; }
    public ProviderBenchmarkRunStatus Status { get; set; } = ProviderBenchmarkRunStatus.Running;
    public string? CompletionCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<ProviderBenchmarkScenario> Scenarios { get; set; } = [];
}

/// <summary>
/// A normalized shot fixture description. It deliberately stores characteristics,
/// not a generation prompt, so the benchmark can be rerun without coupling the
/// persistence model to a provider request shape.
/// </summary>
public sealed class ProviderBenchmarkScenario
{
    public Guid Id { get; set; }
    public Guid ProviderBenchmarkRunId { get; set; }
    public string ScenarioKey { get; set; } = string.Empty;
    public string FixtureRevision { get; set; } = string.Empty;
    public string CharacteristicsHash { get; set; } = string.Empty;
    public string ShotType { get; set; } = string.Empty;
    public string? CameraMotion { get; set; }
    public string? AspectRatio { get; set; }
    public string? TargetResolution { get; set; }
    public int? DurationSeconds { get; set; }
    public int? SubjectCount { get; set; }
    public int? ReferenceFrameCount { get; set; }
    public bool? RequiresCharacterContinuity { get; set; }
    public bool? HasDialogue { get; set; }
    public bool? HasNativeAudio { get; set; }
    public string? CharacteristicsJson { get; set; }
    public string? FixtureProvenanceJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ProviderBenchmarkRun Run { get; set; } = null!;
    public ICollection<ProviderBenchmarkMeasurement> Measurements { get; set; } = [];
}

/// <summary>
/// One empirical observation for one scenario and one provider/model route.
/// Multiple measurement keys are supported for repeated attempts; the same key is
/// an idempotent upsert for a rerunnable harness.
/// </summary>
public sealed class ProviderBenchmarkMeasurement
{
    public Guid Id { get; set; }
    public Guid ProviderBenchmarkRunId { get; set; }
    public Guid ProviderBenchmarkScenarioId { get; set; }
    public string MeasurementKey { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public string ModelKey { get; set; } = string.Empty;
    public ProviderBenchmarkMeasurementStatus Status { get; set; }
    public string? ErrorCode { get; set; }
    public int AttemptNumber { get; set; }
    public int RetryCount { get; set; }
    public long? QueueLatencyMs { get; set; }
    public long? TimeToFirstFrameMs { get; set; }
    public long? GenerationLatencyMs { get; set; }
    public long? TotalLatencyMs { get; set; }
    public int? OutputDurationMs { get; set; }
    public int? OutputWidth { get; set; }
    public int? OutputHeight { get; set; }
    public long? OutputBytes { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public decimal? ActualCostUsd { get; set; }
    public bool ActualCostKnown { get; set; }
    public decimal? QualityScore { get; set; }
    public decimal? ContinuityScore { get; set; }
    public decimal? TemporalStabilityScore { get; set; }
    public decimal? PromptAdherenceScore { get; set; }
    public string Currency { get; set; } = "USD";
    public string? MetricsJson { get; set; }
    public string? ProvenanceJson { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime RecordedAt { get; set; }

    public ProviderBenchmarkRun Run { get; set; } = null!;
    public ProviderBenchmarkScenario Scenario { get; set; } = null!;
    public ICollection<ProviderBenchmarkEvidence> Evidence { get; set; } = [];
}

/// <summary>
/// Evidence associated with a measurement, such as a stored output hash, an
/// automated evaluator report, or a human review record. The reference is an
/// internal locator or opaque evidence identifier, never a provider URL exposed to
/// an end user.
/// </summary>
public sealed class ProviderBenchmarkEvidence
{
    public Guid Id { get; set; }
    public Guid ProviderBenchmarkMeasurementId { get; set; }
    public string EvidenceKey { get; set; } = string.Empty;
    public string EvidenceType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? ContentHash { get; set; }
    public string? PayloadJson { get; set; }
    public string? ProvenanceJson { get; set; }
    public DateTime CapturedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProviderBenchmarkMeasurement Measurement { get; set; } = null!;
}

public sealed record ProviderBenchmarkRunRequest(
    string RunKey,
    string BenchmarkDefinition,
    string? FixtureManifestHash = null,
    string? HarnessVersion = null,
    string? EnvironmentFingerprint = null,
    string? ProvenanceJson = null);

public sealed record ProviderBenchmarkScenarioInput(
    string ScenarioKey,
    string FixtureRevision,
    string CharacteristicsHash,
    string ShotType,
    string? CameraMotion = null,
    string? AspectRatio = null,
    string? TargetResolution = null,
    int? DurationSeconds = null,
    int? SubjectCount = null,
    int? ReferenceFrameCount = null,
    bool? RequiresCharacterContinuity = null,
    bool? HasDialogue = null,
    bool? HasNativeAudio = null,
    string? CharacteristicsJson = null,
    string? FixtureProvenanceJson = null);

public sealed record ProviderBenchmarkMeasurementInput(
    string MeasurementKey,
    string ProviderKey,
    string ModelKey,
    ProviderBenchmarkMeasurementStatus Status,
    int AttemptNumber = 1,
    int RetryCount = 0,
    long? QueueLatencyMs = null,
    long? TimeToFirstFrameMs = null,
    long? GenerationLatencyMs = null,
    long? TotalLatencyMs = null,
    int? OutputDurationMs = null,
    int? OutputWidth = null,
    int? OutputHeight = null,
    long? OutputBytes = null,
    decimal? EstimatedCostUsd = null,
    decimal? ActualCostUsd = null,
    bool ActualCostKnown = false,
    decimal? QualityScore = null,
    decimal? ContinuityScore = null,
    decimal? TemporalStabilityScore = null,
    decimal? PromptAdherenceScore = null,
    string Currency = "USD",
    string? ErrorCode = null,
    string? MetricsJson = null,
    string? ProvenanceJson = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null);

public sealed record ProviderBenchmarkEvidenceInput(
    string EvidenceKey,
    string EvidenceType,
    string Source,
    string? Reference = null,
    string? ContentHash = null,
    string? PayloadJson = null,
    string? ProvenanceJson = null,
    DateTime? CapturedAt = null);

public sealed record ProviderBenchmarkRunCompletion(
    ProviderBenchmarkRunStatus Status,
    string? CompletionCode = null,
    DateTime? CompletedAt = null);

public sealed record ProviderBenchmarkAggregateQuery(
    Guid? RunId = null,
    string? ScenarioKey = null,
    string? CharacteristicsHash = null,
    string? ProviderKey = null,
    string? ModelKey = null);

/// <summary>
/// An aggregate view intentionally reports observations only. It has no winner or
/// preferred-provider field; selection remains a separately governed decision.
/// </summary>
public sealed record ProviderBenchmarkAggregate(
    string ScenarioKey,
    string CharacteristicsHash,
    string ShotType,
    string ProviderKey,
    string ModelKey,
    int SampleCount,
    int SuccessCount,
    decimal SuccessRate,
    double? AverageTotalLatencyMs,
    double? P50TotalLatencyMs,
    decimal? AverageQualityScore,
    decimal? AverageContinuityScore,
    decimal? AverageActualCostUsd);

public interface IProviderBenchmarkService
{
    Task<ProviderBenchmarkRun> GetOrCreateRunAsync(ProviderBenchmarkRunRequest request, CancellationToken cancellationToken = default);
    Task<ProviderBenchmarkScenario> UpsertScenarioAsync(Guid runId, ProviderBenchmarkScenarioInput input, CancellationToken cancellationToken = default);
    Task<ProviderBenchmarkMeasurement> UpsertMeasurementAsync(Guid runId, Guid scenarioId, ProviderBenchmarkMeasurementInput input, CancellationToken cancellationToken = default);
    Task<ProviderBenchmarkEvidence> UpsertEvidenceAsync(Guid runId, Guid measurementId, ProviderBenchmarkEvidenceInput input, CancellationToken cancellationToken = default);
    Task<ProviderBenchmarkRun> CompleteRunAsync(Guid runId, ProviderBenchmarkRunCompletion completion, CancellationToken cancellationToken = default);
    Task<ProviderBenchmarkRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderBenchmarkAggregate>> GetAggregatesAsync(ProviderBenchmarkAggregateQuery query, CancellationToken cancellationToken = default);
}

internal static class ProviderBenchmarkValidation
{
    public static string Required(string value, string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var normalized = value.Trim();
        if (normalized.Length > maxLength) throw new ArgumentException($"{name} exceeds the maximum length.", name);
        return normalized;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength) throw new ArgumentException($"{name} exceeds the maximum length.", name);
        return normalized;
    }

    public static string? Json(string? value, string name, int maxLength)
    {
        var normalized = Optional(value, name, maxLength);
        if (normalized is null) return null;
        try
        {
            using var document = JsonDocument.Parse(normalized);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} must contain valid JSON.", name, exception);
        }

        return normalized;
    }

    public static void NonNegative(long? value, string name)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(name, value, "The value cannot be negative.");
    }

    public static void NonNegative(int? value, string name)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(name, value, "The value cannot be negative.");
    }

    public static void NonNegative(decimal? value, string name)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(name, value, "The value cannot be negative.");
    }

    public static void Score(decimal? value, string name)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(name, value, "The score must be between 0 and 100.");
    }
}

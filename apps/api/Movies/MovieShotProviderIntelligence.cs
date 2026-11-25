namespace Taslim.Api.Movies;

/// <summary>
/// Stable, provider-neutral shot dimensions used by the benchmark scorer. These describe
/// observable shot requirements rather than vendor controls or model capabilities.
/// </summary>
public static class MovieShotBenchmarkTypes
{
    public const string CloseUpFace = "close_up_face";
    public const string Motion = "motion";
    public const string CameraMovement = "camera_movement";
    public const string HandsBody = "hands_body";
    public const string Environment = "environment";
    public const string Continuity = "continuity";
    public const string Vfx = "vfx";
    public const string TextSignage = "text_signage";
    public const string LipSync = "lip_sync";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        CloseUpFace,
        Motion,
        CameraMovement,
        HandsBody,
        Environment,
        Continuity,
        Vfx,
        TextSignage,
        LipSync,
    };
}

/// <summary>
/// Provenance for a returned suitability value. Unknown is intentionally distinct from a
/// low score, and fallback is never presented as historical benchmark evidence.
/// </summary>
public enum MovieShotBenchmarkEvidenceKind
{
    Unknown = 0,
    Fallback = 1,
    Mixed = 2,
    Empirical = 3,
}

/// <summary>
/// A normalized shot requirement supplied by a planner. Importance is a ranking weight,
/// not a provider or model setting.
/// </summary>
public sealed record MovieShotBenchmarkRequirement(string ShotType, int Importance = 50);

/// <summary>
/// An aggregate of historical benchmark observations for one opaque candidate and shot type.
/// The candidate key is deliberately opaque to this contract; no provider or model metadata
/// is required or returned here.
/// </summary>
public sealed record MovieShotBenchmarkEvidence(
    string CandidateKey,
    string ShotType,
    decimal SuitabilityScore,
    int SampleCount,
    decimal Confidence,
    DateTime ObservedAtUtc,
    string BenchmarkVersion,
    string EvidenceReference);

/// <summary>
/// An explicitly supplied non-empirical fallback. Fallback data is useful for cold-start
/// behavior, but it is never merged into empirical evidence or relabeled as historical data.
/// </summary>
public sealed record MovieShotBenchmarkFallback(
    string ShotType,
    decimal SuitabilityScore,
    decimal Confidence,
    string Reason);

public sealed record MovieShotBenchmarkRequest(
    IReadOnlyList<string> CandidateKeys,
    IReadOnlyList<MovieShotBenchmarkRequirement> Requirements,
    IReadOnlyList<MovieShotBenchmarkFallback>? Fallbacks = null);

public sealed record MovieShotBenchmarkValidationError(string Code, string Field, string Message);

public sealed class MovieShotBenchmarkValidationException(
    IReadOnlyList<MovieShotBenchmarkValidationError> errors)
    : Exception("The shot benchmark request or evidence is invalid.")
{
    public IReadOnlyList<MovieShotBenchmarkValidationError> Errors { get; } = errors;
}

/// <summary>
/// The score for one required shot dimension. A null score means no empirical or explicit
/// fallback value was available; it must not be interpreted as zero suitability.
/// </summary>
public sealed record MovieShotBenchmarkDimensionScore(
    string ShotType,
    int Importance,
    decimal? SuitabilityScore,
    decimal Confidence,
    int SampleCount,
    MovieShotBenchmarkEvidenceKind EvidenceKind,
    string? EvidenceReference,
    string? BenchmarkVersion,
    DateTime? ObservedAtUtc,
    string? Reason);

public sealed record MovieShotBenchmarkCandidateScore(
    string CandidateKey,
    decimal? OverallSuitabilityScore,
    decimal Confidence,
    decimal EvidenceCoverage,
    int EmpiricalDimensionCount,
    int FallbackDimensionCount,
    int UnknownDimensionCount,
    MovieShotBenchmarkEvidenceKind EvidenceKind,
    IReadOnlyList<MovieShotBenchmarkDimensionScore> Dimensions);

public sealed record MovieShotBenchmarkRankingResult(
    string ContractVersion,
    IReadOnlyList<string> RequestedShotTypes,
    IReadOnlyList<MovieShotBenchmarkCandidateScore> RankedCandidates);

/// <summary>
/// Pure benchmark scorer. It consumes historical evidence already available to the caller;
/// it never invokes a provider, reads provider credentials, or estimates provider cost.
/// </summary>
public sealed class MovieShotBenchmarkScorer
{
    public const string ContractVersion = "movie-shot-intelligence-v1";
    public const int MaximumCandidates = 64;
    public const int MaximumRequirements = 16;
    public const int MaximumEvidenceItems = 1_024;
    public const int MaximumKeyLength = 120;
    public const int MaximumTextLength = 240;

    public MovieShotBenchmarkRankingResult Rank(
        MovieShotBenchmarkRequest request,
        IReadOnlyList<MovieShotBenchmarkEvidence>? empiricalEvidence = null)
    {
        var errors = Validate(request, empiricalEvidence);
        if (errors.Count > 0) throw new MovieShotBenchmarkValidationException(errors);

        var evidenceByKey = (empiricalEvidence ?? [])
            .ToDictionary(item => (item.CandidateKey, item.ShotType));
        var fallbacksByShotType = (request.Fallbacks ?? [])
            .ToDictionary(item => item.ShotType, StringComparer.Ordinal);
        var ranked = request.CandidateKeys
            .Select(candidateKey => ScoreCandidate(candidateKey, request.Requirements, evidenceByKey, fallbacksByShotType))
            .OrderByDescending(item => EvidenceRank(item.EvidenceKind))
            .ThenByDescending(item => item.OverallSuitabilityScore.HasValue)
            .ThenByDescending(item => item.OverallSuitabilityScore ?? decimal.MinValue)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
            .ToArray();

        return new MovieShotBenchmarkRankingResult(
            ContractVersion,
            request.Requirements.Select(item => item.ShotType).ToArray(),
            ranked);
    }

    public static IReadOnlyList<MovieShotBenchmarkValidationError> Validate(
        MovieShotBenchmarkRequest? request,
        IReadOnlyList<MovieShotBenchmarkEvidence>? empiricalEvidence = null)
    {
        var errors = new List<MovieShotBenchmarkValidationError>();
        if (request is null)
        {
            errors.Add(new("required", "request", "A benchmark request is required."));
            return errors;
        }

        if (request.CandidateKeys is null || request.CandidateKeys.Count == 0)
            errors.Add(new("required", nameof(request.CandidateKeys), "At least one candidate is required."));
        else if (request.CandidateKeys.Count > MaximumCandidates)
            errors.Add(new("too_many_items", nameof(request.CandidateKeys), $"No more than {MaximumCandidates} candidates are allowed."));

        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var requestedCandidates = request.CandidateKeys ?? [];
        for (var index = 0; index < requestedCandidates.Count; index++)
        {
            var key = requestedCandidates[index];
            var field = $"{nameof(request.CandidateKeys)}[{index}]";
            ValidateText(errors, field, key, MaximumKeyLength, "Candidate key");
            if (!string.IsNullOrWhiteSpace(key) && !candidateKeys.Add(key))
                errors.Add(new("duplicate", field, "Candidate keys must be unique."));
        }

        if (request.Requirements is null || request.Requirements.Count == 0)
            errors.Add(new("required", nameof(request.Requirements), "At least one shot-type requirement is required."));
        else if (request.Requirements.Count > MaximumRequirements)
            errors.Add(new("too_many_items", nameof(request.Requirements), $"No more than {MaximumRequirements} shot-type requirements are allowed."));

        var requiredShotTypes = new HashSet<string>(StringComparer.Ordinal);
        var requestedRequirements = request.Requirements ?? [];
        for (var index = 0; index < requestedRequirements.Count; index++)
        {
            var requirement = requestedRequirements[index];
            var field = $"{nameof(request.Requirements)}[{index}]";
            if (requirement is null)
            {
                errors.Add(new("required", field, "A shot-type requirement is required."));
                continue;
            }

            ValidateShotType(errors, $"{field}.{nameof(requirement.ShotType)}", requirement.ShotType);
            if (!string.IsNullOrWhiteSpace(requirement.ShotType) && !requiredShotTypes.Add(requirement.ShotType))
                errors.Add(new("duplicate", $"{field}.{nameof(requirement.ShotType)}", "Each shot type may appear only once."));
            if (requirement.Importance is < 0 or > 100)
                errors.Add(new("out_of_range", $"{field}.{nameof(requirement.Importance)}", "Importance must be between 0 and 100."));
        }

        ValidateEvidence(errors, empiricalEvidence, candidateKeys, requiredShotTypes);
        ValidateFallbacks(errors, request.Fallbacks, requiredShotTypes);
        return errors;
    }

    private static MovieShotBenchmarkCandidateScore ScoreCandidate(
        string candidateKey,
        IReadOnlyList<MovieShotBenchmarkRequirement> requirements,
        IReadOnlyDictionary<(string CandidateKey, string ShotType), MovieShotBenchmarkEvidence> evidenceByKey,
        IReadOnlyDictionary<string, MovieShotBenchmarkFallback> fallbacksByShotType)
    {
        var dimensions = requirements.Select(requirement =>
        {
            if (evidenceByKey.TryGetValue((candidateKey, requirement.ShotType), out var evidence))
            {
                return new MovieShotBenchmarkDimensionScore(
                    requirement.ShotType,
                    requirement.Importance,
                    evidence.SuitabilityScore,
                    evidence.Confidence,
                    evidence.SampleCount,
                    MovieShotBenchmarkEvidenceKind.Empirical,
                    evidence.EvidenceReference,
                    evidence.BenchmarkVersion,
                    evidence.ObservedAtUtc,
                    null);
            }

            if (fallbacksByShotType.TryGetValue(requirement.ShotType, out var fallback))
            {
                return new MovieShotBenchmarkDimensionScore(
                    requirement.ShotType,
                    requirement.Importance,
                    fallback.SuitabilityScore,
                    fallback.Confidence,
                    0,
                    MovieShotBenchmarkEvidenceKind.Fallback,
                    null,
                    null,
                    null,
                    fallback.Reason);
            }

            return new MovieShotBenchmarkDimensionScore(
                requirement.ShotType,
                requirement.Importance,
                null,
                0m,
                0,
                MovieShotBenchmarkEvidenceKind.Unknown,
                null,
                null,
                null,
                "No empirical or fallback evidence is available.");
        }).ToArray();

        var totalWeight = dimensions.Sum(item => Weight(item.Importance));
        var known = dimensions.Where(item => item.SuitabilityScore.HasValue).ToArray();
        var knownWeight = known.Sum(item => Weight(item.Importance));
        var score = known.Length == 0
            ? (decimal?)null
            : decimal.Round(known.Sum(item => item.SuitabilityScore!.Value * Weight(item.Importance)) / knownWeight, 3);
        var coverage = totalWeight == 0m ? 0m : decimal.Round(knownWeight / totalWeight, 3);
        var confidence = known.Length == 0
            ? 0m
            : decimal.Round(known.Sum(item => item.Confidence * Weight(item.Importance)) / totalWeight, 3);
        var empiricalCount = dimensions.Count(item => item.EvidenceKind == MovieShotBenchmarkEvidenceKind.Empirical);
        var fallbackCount = dimensions.Count(item => item.EvidenceKind == MovieShotBenchmarkEvidenceKind.Fallback);
        var unknownCount = dimensions.Count(item => item.EvidenceKind == MovieShotBenchmarkEvidenceKind.Unknown);
        var kind = empiricalCount == dimensions.Length
            ? MovieShotBenchmarkEvidenceKind.Empirical
            : known.Length == 0
                ? MovieShotBenchmarkEvidenceKind.Unknown
                : empiricalCount == 0
                    ? MovieShotBenchmarkEvidenceKind.Fallback
                    : MovieShotBenchmarkEvidenceKind.Mixed;

        return new MovieShotBenchmarkCandidateScore(
            candidateKey,
            score,
            confidence,
            coverage,
            empiricalCount,
            fallbackCount,
            unknownCount,
            kind,
            dimensions);
    }

    private static decimal Weight(int importance) => Math.Max(1, importance);

    private static int EvidenceRank(MovieShotBenchmarkEvidenceKind kind) => kind switch
    {
        MovieShotBenchmarkEvidenceKind.Empirical => 3,
        MovieShotBenchmarkEvidenceKind.Mixed => 2,
        MovieShotBenchmarkEvidenceKind.Fallback => 1,
        _ => 0,
    };

    private static void ValidateEvidence(
        List<MovieShotBenchmarkValidationError> errors,
        IReadOnlyList<MovieShotBenchmarkEvidence>? evidence,
        IReadOnlySet<string> candidateKeys,
        IReadOnlySet<string> requiredShotTypes)
    {
        if (evidence is null) return;
        if (evidence.Count > MaximumEvidenceItems)
            errors.Add(new("too_many_items", nameof(evidence), $"No more than {MaximumEvidenceItems} evidence items are allowed."));

        var seen = new HashSet<(string CandidateKey, string ShotType)>();
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            var field = $"empiricalEvidence[{index}]";
            if (item is null)
            {
                errors.Add(new("required", field, "An evidence item is required."));
                continue;
            }

            ValidateText(errors, $"{field}.{nameof(item.CandidateKey)}", item.CandidateKey, MaximumKeyLength, "Candidate key");
            ValidateShotType(errors, $"{field}.{nameof(item.ShotType)}", item.ShotType);
            if (!candidateKeys.Contains(item.CandidateKey))
                errors.Add(new("unknown_reference", $"{field}.{nameof(item.CandidateKey)}", "Evidence must reference a requested candidate."));
            if (!requiredShotTypes.Contains(item.ShotType))
                errors.Add(new("unknown_reference", $"{field}.{nameof(item.ShotType)}", "Evidence must reference a requested shot type."));
            if (!seen.Add((item.CandidateKey, item.ShotType)))
                errors.Add(new("duplicate", field, "Only one aggregate evidence item is allowed per candidate and shot type."));
            ValidateScore(errors, $"{field}.{nameof(item.SuitabilityScore)}", item.SuitabilityScore);
            if (item.SampleCount < 1)
                errors.Add(new("out_of_range", $"{field}.{nameof(item.SampleCount)}", "Sample count must be greater than zero for empirical evidence."));
            ValidateScore(errors, $"{field}.{nameof(item.Confidence)}", item.Confidence);
            ValidateText(errors, $"{field}.{nameof(item.BenchmarkVersion)}", item.BenchmarkVersion, MaximumTextLength, "Benchmark version");
            ValidateText(errors, $"{field}.{nameof(item.EvidenceReference)}", item.EvidenceReference, MaximumTextLength, "Evidence reference");
            if (item.ObservedAtUtc == default)
                errors.Add(new("required", $"{field}.{nameof(item.ObservedAtUtc)}", "Observed-at time is required for empirical evidence."));
        }
    }

    private static void ValidateFallbacks(
        List<MovieShotBenchmarkValidationError> errors,
        IReadOnlyList<MovieShotBenchmarkFallback>? fallbacks,
        IReadOnlySet<string> requiredShotTypes)
    {
        if (fallbacks is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fallbacks.Count; index++)
        {
            var item = fallbacks[index];
            var field = $"{nameof(fallbacks)}[{index}]";
            if (item is null)
            {
                errors.Add(new("required", field, "A fallback item is required."));
                continue;
            }

            ValidateShotType(errors, $"{field}.{nameof(item.ShotType)}", item.ShotType);
            if (!requiredShotTypes.Contains(item.ShotType))
                errors.Add(new("unknown_reference", $"{field}.{nameof(item.ShotType)}", "Fallback must reference a requested shot type."));
            if (!seen.Add(item.ShotType))
                errors.Add(new("duplicate", field, "Only one fallback is allowed per shot type."));
            ValidateScore(errors, $"{field}.{nameof(item.SuitabilityScore)}", item.SuitabilityScore);
            ValidateScore(errors, $"{field}.{nameof(item.Confidence)}", item.Confidence);
            ValidateText(errors, $"{field}.{nameof(item.Reason)}", item.Reason, MaximumTextLength, "Fallback reason");
        }
    }

    private static void ValidateShotType(List<MovieShotBenchmarkValidationError> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new("required", field, "Shot type is required."));
            return;
        }
        if (!MovieShotBenchmarkTypes.Supported.Contains(value))
            errors.Add(new("unsupported_value", field, "Shot type is not supported."));
    }

    private static void ValidateScore(List<MovieShotBenchmarkValidationError> errors, string field, decimal value)
    {
        if (value is < 0m or > 1m)
            errors.Add(new("out_of_range", field, "Score must be between 0 and 1."));
    }

    private static void ValidateText(List<MovieShotBenchmarkValidationError> errors, string field, string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(new("required", field, $"{label} is required."));
        else if (value.Length > maximum)
            errors.Add(new("too_long", field, $"{label} must be {maximum} characters or fewer."));
    }
}

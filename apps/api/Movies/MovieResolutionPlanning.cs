namespace Taslim.Api.Movies;

/// <summary>
/// The supported delivery tiers. The planner deliberately uses Taslim-owned
/// labels rather than provider or model identifiers.
/// </summary>
public static class MovieResolutionTiers
{
    public const string P480 = "480p";
    public const string P720 = "720p";
    public const string P1080 = "1080p";
    public const string P1440 = "1440p";
    public const string P2160 = "2160p";

    public static readonly IReadOnlyList<MovieResolutionTier> All =
    [
        new(P480, 854, 480, "480p", 0),
        new(P720, 1280, 720, "720p", 1),
        new(P1080, 1920, 1080, "1080p", 2),
        new(P1440, 2560, 1440, "1440p / 2K", 3),
        new(P2160, 3840, 2160, "2160p / 4K", 4),
    ];

    public static bool TryGet(string? value, out MovieResolutionTier tier)
    {
        tier = All.FirstOrDefault(item => string.Equals(item.Code, value?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return tier is not null;
    }
}

public sealed record MovieResolutionTier(string Code, int Width, int Height, string DisplayLabel, int Order);

public static class MovieResolutionPathKinds
{
    public const string Native = "native";
    public const string SourceToMaster = "source_to_master";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Native,
        SourceToMaster,
    };
}

public static class MovieResolutionEvidenceStatuses
{
    public const string Known = "known";
    public const string Missing = "missing";
    public const string Stale = "stale";
    public const string Unsupported = "unsupported";
    public const string Invalid = "invalid";
}

public static class MovieResolutionPlanStatuses
{
    public const string Recommended = "recommended";
    public const string InsufficientEvidence = "insufficient_evidence";
    public const string UnsupportedTarget = "unsupported_target";
    public const string InvalidRequest = "invalid_request";
}

public static class MovieResolutionPlanReasonCodes
{
    public const string SelectedLowestCost = "selected_lowest_cost";
    public const string EvidenceMissing = "evidence_missing";
    public const string EvidenceStale = "evidence_stale";
    public const string EvidenceUnsupported = "evidence_unsupported";
    public const string EvidenceInvalid = "evidence_invalid";
    public const string CostUnknown = "cost_unknown";
    public const string QualityEvidenceUnknown = "quality_evidence_unknown";
    public const string QualityBelowRequirement = "quality_below_requirement";
    public const string BenchmarkSampleInsufficient = "benchmark_sample_insufficient";
    public const string PathUnavailable = "path_unavailable";
    public const string SourceResolutionRequired = "source_resolution_required";
    public const string SourceResolutionMustBeBelowTarget = "source_resolution_must_be_below_target";
    public const string TargetMismatch = "target_mismatch";
    public const string UnsupportedPath = "unsupported_path";
    public const string SourceResolutionUnknown = "source_resolution_unknown";
    public const string BenchmarkRefreshRequired = "benchmark_refresh_required";
    public const string QualityReviewRequired = "quality_review_required";
    public const string NativeEscalation = "escalate_to_native_if_master_qc_fails";
}

/// <summary>
/// Provider/model-neutral evidence produced by a benchmark adapter or a
/// reviewed operator snapshot. One record describes the predicted final master
/// for one path; it does not authorize generation.
/// </summary>
public sealed record MovieResolutionEvidence(
    string PathKind,
    string TargetResolution,
    string? SourceResolution,
    bool IsAvailable,
    decimal? GenerationCostUsd,
    decimal? MasteringCostUsd,
    decimal? PredictedQualityScore,
    int BenchmarkSampleCount,
    DateTime? BenchmarkedAtUtc,
    string EvidenceStatus = MovieResolutionEvidenceStatuses.Known);

public sealed class MovieResolutionPlannerOptions
{
    public decimal DefaultMinimumQualityScore { get; set; } = 0.80m;
    public int MinimumBenchmarkSamples { get; set; } = 3;
    public int MaximumBenchmarkAgeDays { get; set; } = 90;
    public decimal NarrowQualityMargin { get; set; } = 0.05m;
}

public sealed record MovieResolutionPlanningRequest(
    string TargetResolution,
    string? SourceResolution,
    decimal? MinimumQualityScore,
    IReadOnlyList<MovieResolutionEvidence> Evidence);

public sealed record MovieResolutionPlanPath(
    string PathKind,
    string? SourceResolution,
    string TargetResolution,
    bool IsEligible,
    decimal? EstimatedCostUsd,
    decimal? PredictedQualityScore,
    string ReasonCode,
    IReadOnlyList<string> EscalationTriggers);

public sealed record MovieResolutionPlan(
    string Status,
    string TargetResolution,
    string? SourceResolution,
    decimal MinimumQualityScore,
    MovieResolutionPlanPath? SelectedPath,
    IReadOnlyList<MovieResolutionPlanPath> Alternatives,
    IReadOnlyList<string> EscalationTriggers,
    string? FailureReasonCode = null);

public interface IMovieResolutionPlanner
{
    MovieResolutionPlan Plan(MovieResolutionPlanningRequest request, DateTime? asOfUtc = null);
}

/// <summary>
/// Selects the least expensive evidenced path that is predicted to meet the
/// requested quality. Unknown cost or quality is never treated as zero or as a
/// pass. This is intentionally a pure planner: it performs no provider calls,
/// queues no jobs, and persists no state.
/// </summary>
public sealed class MovieResolutionPlanner(MovieResolutionPlannerOptions? configuredOptions = null) : IMovieResolutionPlanner
{
    private readonly MovieResolutionPlannerOptions options = configuredOptions ?? new MovieResolutionPlannerOptions();

    public MovieResolutionPlan Plan(MovieResolutionPlanningRequest request, DateTime? asOfUtc = null)
    {
        var target = FindTier(request.TargetResolution);
        var source = FindTier(request.SourceResolution);
        var minimumQuality = request.MinimumQualityScore ?? options.DefaultMinimumQualityScore;
        var now = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();

        if (target is null || minimumQuality is < 0m or > 1m)
        {
            return new(
                MovieResolutionPlanStatuses.InvalidRequest,
                target?.Code ?? request.TargetResolution?.Trim() ?? string.Empty,
                source?.Code,
                Math.Clamp(minimumQuality, 0m, 1m),
                null,
                [],
                [],
                target is null ? MovieResolutionPlanReasonCodes.TargetMismatch : MovieResolutionPlanReasonCodes.QualityEvidenceUnknown);
        }

        if (source is null && !string.IsNullOrWhiteSpace(request.SourceResolution))
        {
            return new(
                MovieResolutionPlanStatuses.InvalidRequest,
                target.Code,
                request.SourceResolution.Trim(),
                minimumQuality,
                null,
                [],
                [],
                MovieResolutionPlanReasonCodes.SourceResolutionUnknown);
        }

        if (source is not null && source.Order >= target.Order)
        {
            return new(
                MovieResolutionPlanStatuses.InvalidRequest,
                target.Code,
                source.Code,
                minimumQuality,
                null,
                [],
                [],
                MovieResolutionPlanReasonCodes.SourceResolutionMustBeBelowTarget);
        }

        var expected = BuildExpectedPaths(target, source);
        var evidence = request.Evidence ?? [];
        var paths = expected
            .Select(path => Evaluate(path, evidence, target, source, minimumQuality, now))
            .ToArray();

        var eligible = paths
            .Where(item => item.IsEligible)
            .OrderBy(item => item.EstimatedCostUsd)
            .ThenBy(item => item.PathKind == MovieResolutionPathKinds.Native ? 0 : 1)
            .ThenBy(item => item.SourceResolution is null ? int.MaxValue : FindTier(item.SourceResolution)!.Order)
            .ToArray();

        if (eligible.Length == 0)
        {
            var triggers = BuildInsufficientEvidenceTriggers(paths);
            return new(
                MovieResolutionPlanStatuses.InsufficientEvidence,
                target.Code,
                source?.Code,
                minimumQuality,
                null,
                paths,
                triggers,
                FindFailureReason(paths));
        }

        var selected = eligible[0] with
        {
            ReasonCode = MovieResolutionPlanReasonCodes.SelectedLowestCost,
            EscalationTriggers = BuildSelectionTriggers(eligible[0], minimumQuality),
        };
        var alternatives = paths
            .Select(path => path == eligible[0] ? selected : path)
            .ToArray();
        var planTriggers = selected.EscalationTriggers;
        return new(
            MovieResolutionPlanStatuses.Recommended,
            target.Code,
            source?.Code,
            minimumQuality,
            selected,
            alternatives,
            planTriggers);
    }

    private MovieResolutionPlanPath Evaluate(
        ExpectedPath path,
        IReadOnlyList<MovieResolutionEvidence> allEvidence,
        MovieResolutionTier target,
        MovieResolutionTier? source,
        decimal minimumQuality,
        DateTime asOfUtc)
    {
        var candidate = allEvidence
            .Where(item => Matches(item, path, target, source))
            .OrderByDescending(item => item.BenchmarkedAtUtc ?? DateTime.MinValue)
            .ThenBy(item => TotalCost(item) ?? decimal.MaxValue)
            .FirstOrDefault();

        if (candidate is null)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.EvidenceMissing);
        }

        if (!MovieResolutionPathKinds.Supported.Contains(candidate.PathKind?.Trim() ?? string.Empty))
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.UnsupportedPath);
        }

        if (!candidate.IsAvailable)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.PathUnavailable);
        }

        var status = candidate.EvidenceStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!string.Equals(status, MovieResolutionEvidenceStatuses.Known, StringComparison.Ordinal))
        {
            return Ineligible(path, status switch
            {
                MovieResolutionEvidenceStatuses.Stale => MovieResolutionPlanReasonCodes.EvidenceStale,
                MovieResolutionEvidenceStatuses.Unsupported => MovieResolutionPlanReasonCodes.EvidenceUnsupported,
                _ => MovieResolutionPlanReasonCodes.EvidenceInvalid,
            });
        }

        if (candidate.BenchmarkSampleCount < Math.Max(1, options.MinimumBenchmarkSamples))
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.BenchmarkSampleInsufficient);
        }

        if (!candidate.BenchmarkedAtUtc.HasValue || candidate.BenchmarkedAtUtc.Value.ToUniversalTime() < asOfUtc.AddDays(-Math.Max(1, options.MaximumBenchmarkAgeDays)))
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.EvidenceStale);
        }

        var totalCost = TotalCost(candidate);
        if (!totalCost.HasValue)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.CostUnknown);
        }

        if (!candidate.PredictedQualityScore.HasValue)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.QualityEvidenceUnknown) with
            {
                EstimatedCostUsd = totalCost,
            };
        }

        if (candidate.PredictedQualityScore.Value is < 0m or > 1m)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.EvidenceInvalid) with
            {
                EstimatedCostUsd = totalCost,
            };
        }

        if (candidate.PredictedQualityScore.Value < minimumQuality)
        {
            return Ineligible(path, MovieResolutionPlanReasonCodes.QualityBelowRequirement) with
            {
                EstimatedCostUsd = totalCost,
                PredictedQualityScore = candidate.PredictedQualityScore,
            };
        }

        return new(
            path.Kind,
            path.SourceResolution,
            target.Code,
            true,
            totalCost,
            candidate.PredictedQualityScore,
            MovieResolutionPlanReasonCodes.SelectedLowestCost,
            []);
    }

    private static bool Matches(MovieResolutionEvidence evidence, ExpectedPath path, MovieResolutionTier target, MovieResolutionTier? source)
    {
        if (!string.Equals(evidence.TargetResolution?.Trim(), target.Code, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(evidence.PathKind?.Trim(), path.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Kind == MovieResolutionPathKinds.Native)
        {
            return string.IsNullOrWhiteSpace(evidence.SourceResolution);
        }

        return source is not null && string.Equals(evidence.SourceResolution?.Trim(), source.Code, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? TotalCost(MovieResolutionEvidence evidence)
    {
        if (evidence.GenerationCostUsd is < 0m || evidence.MasteringCostUsd is < 0m) return null;
        if (!evidence.GenerationCostUsd.HasValue || !evidence.MasteringCostUsd.HasValue) return null;
        return decimal.Round(evidence.GenerationCostUsd.Value + evidence.MasteringCostUsd.Value, 4);
    }

    private static IReadOnlyList<ExpectedPath> BuildExpectedPaths(MovieResolutionTier target, MovieResolutionTier? source) =>
    [
        new(MovieResolutionPathKinds.Native, null, target.Code),
        .. source is null ? [] : new[] { new ExpectedPath(MovieResolutionPathKinds.SourceToMaster, source.Code, target.Code) },
    ];

    private static MovieResolutionPlanPath Ineligible(ExpectedPath path, string reason) =>
        new(path.Kind, path.SourceResolution, path.TargetResolution, false, null, null, reason, []);

    private IReadOnlyList<string> BuildSelectionTriggers(MovieResolutionPlanPath selected, decimal minimumQuality)
    {
        var triggers = new List<string>();
        if (selected.PathKind == MovieResolutionPathKinds.SourceToMaster)
        {
            triggers.Add(MovieResolutionPlanReasonCodes.NativeEscalation);
            triggers.Add(MovieResolutionPlanReasonCodes.QualityReviewRequired);
        }
        if (selected.PredictedQualityScore.HasValue && selected.PredictedQualityScore.Value - minimumQuality < Math.Max(0m, options.NarrowQualityMargin))
        {
            triggers.Add(MovieResolutionPlanReasonCodes.QualityReviewRequired);
        }
        return triggers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> BuildInsufficientEvidenceTriggers(IReadOnlyList<MovieResolutionPlanPath> paths)
    {
        var triggers = new List<string> { MovieResolutionPlanReasonCodes.BenchmarkRefreshRequired };
        if (paths.Any(item => item.ReasonCode is MovieResolutionPlanReasonCodes.QualityEvidenceUnknown or MovieResolutionPlanReasonCodes.QualityBelowRequirement))
        {
            triggers.Add(MovieResolutionPlanReasonCodes.QualityReviewRequired);
        }
        return triggers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string FindFailureReason(IReadOnlyList<MovieResolutionPlanPath> paths) =>
        paths.FirstOrDefault(item => item.ReasonCode == MovieResolutionPlanReasonCodes.QualityBelowRequirement)?.ReasonCode
        ?? paths.FirstOrDefault(item => item.ReasonCode == MovieResolutionPlanReasonCodes.CostUnknown)?.ReasonCode
        ?? paths.FirstOrDefault()?.ReasonCode
        ?? MovieResolutionPlanReasonCodes.EvidenceMissing;

    private static MovieResolutionTier? FindTier(string? value) => MovieResolutionTiers.TryGet(value, out var tier) ? tier : null;

    private sealed record ExpectedPath(string Kind, string? SourceResolution, string TargetResolution);
}

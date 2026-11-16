using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.Movies;

/// <summary>
/// Stable, provider-neutral actions emitted by the movie production QC boundary.
/// These are internal workflow actions, not provider or model controls.
/// </summary>
public static class MovieProductionQcActions
{
    public const string Accept = "accept";
    public const string Upscale = "upscale";
    public const string RegenerateAtHigherSourceResolution = "regenerate_higher_source_resolution";
    public const string RequireReview = "require_review";
}

public static class MovieProductionQcReasonCodes
{
    public const string Accepted = "MOVIE_QC_ACCEPTED";
    public const string MeasurementMissing = "MOVIE_QC_MEASUREMENT_MISSING";
    public const string MeasurementInvalid = "MOVIE_QC_MEASUREMENT_INVALID";
    public const string AspectRatioMismatch = "MOVIE_QC_ASPECT_RATIO_MISMATCH";
    public const string DurationMissing = "MOVIE_QC_DURATION_MISSING";
    public const string DurationOutOfTolerance = "MOVIE_QC_DURATION_OUT_OF_TOLERANCE";
    public const string ContinuityEvidenceMissing = "MOVIE_QC_CONTINUITY_EVIDENCE_MISSING";
    public const string ContinuityHashMismatch = "MOVIE_QC_CONTINUITY_HASH_MISMATCH";
    public const string HardContinuityConflict = "MOVIE_QC_HARD_CONTINUITY_CONFLICT";
    public const string ContinuityWarningsExceeded = "MOVIE_QC_CONTINUITY_WARNINGS_EXCEEDED";
    public const string TargetResolutionUnmet = "MOVIE_QC_TARGET_RESOLUTION_UNMET";
    public const string UpscaleWithinBounds = "MOVIE_QC_UPSCALE_WITHIN_BOUNDS";
    public const string SourceResolutionInsufficient = "MOVIE_QC_SOURCE_RESOLUTION_INSUFFICIENT";
    public const string EscalationCostUnknown = "MOVIE_QC_ESCALATION_COST_UNKNOWN";
    public const string EscalationCostLimitExceeded = "MOVIE_QC_ESCALATION_COST_LIMIT_EXCEEDED";
}

public sealed class MovieProductionQualityControlOptions
{
    public int MaxFindings { get; set; } = 16;
    public int MaxResolutionDimension { get; set; } = 16_384;
    public int DefaultDurationToleranceSeconds { get; set; } = 1;
    public decimal DefaultMaxUpscaleFactor { get; set; } = 2m;
    public decimal DefaultAspectRatioTolerance { get; set; } = 0.01m;
    public int DefaultMaxContinuityWarnings { get; set; }
    public bool RequireKnownEscalationCost { get; set; } = true;
}

public sealed record MovieResolution(int Width, int Height)
{
    public long PixelCount => (long)Math.Max(0, Width) * Math.Max(0, Height);

    public decimal ScaleRequiredFor(MovieResolution target)
    {
        if (Width <= 0 || Height <= 0 || target.Width <= 0 || target.Height <= 0) return decimal.MaxValue;
        return Math.Max(target.Width / (decimal)Width, target.Height / (decimal)Height);
    }

    public decimal AspectRatio => Width > 0 && Height > 0 ? Width / (decimal)Height : 0m;
}

public sealed record MovieProductionQcRequirements(
    MovieResolution TargetResolution,
    MovieResolution MinimumSourceResolution,
    int ExpectedDurationSeconds,
    int? DurationToleranceSeconds = null,
    decimal? MaxUpscaleFactor = null,
    decimal? AspectRatioTolerance = null,
    string? RequiredContinuitySnapshotHash = null,
    int? MaxContinuityWarnings = null,
    bool RequireContinuityEvidence = true,
    MovieResolution? RecommendedRegenerationSourceResolution = null);

/// <summary>
/// Economics are estimates only. This contract never charges, reserves, or calls a provider.
/// Unknown or over-limit escalation cost is deliberately converted to review.
/// </summary>
public sealed record MovieProductionQcEconomics(
    decimal? MaxAdditionalCostUsd = null,
    decimal? EstimatedUpscaleCostUsd = null,
    decimal? EstimatedRegenerationCostUsd = null,
    bool? RequireKnownEscalationCost = null);

public sealed record MovieProductionQcEvidence(
    MovieResolution? CurrentResolution,
    int? DurationSeconds,
    string? ContinuitySnapshotHash,
    int ContinuityWarningCount = 0,
    int HardContinuityConflictCount = 0,
    string? OutputSha256 = null);

public sealed record MovieProductionQcRequest(
    MovieProductionQcRequirements Requirements,
    MovieProductionQcEvidence Evidence,
    MovieProductionQcEconomics? Economics = null);

public sealed record MovieProductionQcFinding(
    string ReasonCode,
    string Category,
    string Severity,
    string? Expected = null,
    string? Actual = null);

public sealed record MovieProductionQcDecision(
    string ContractVersion,
    string Action,
    bool RequiresHumanReview,
    IReadOnlyList<MovieProductionQcFinding> Findings,
    MovieResolution? CurrentResolution,
    MovieResolution TargetResolution,
    MovieResolution? RecommendedSourceResolution,
    decimal? EstimatedAdditionalCostUsd)
{
    public bool IsAccepted => string.Equals(Action, MovieProductionQcActions.Accept, StringComparison.Ordinal);
    public bool IsEscalation => !IsAccepted;
}

public interface IMovieProductionQualityControl
{
    MovieProductionQcDecision Evaluate(MovieProductionQcRequest request);
}

/// <summary>
/// Deterministic production QC policy. It only evaluates supplied measurements and persisted
/// continuity evidence; it does not infer artistic merit, call a provider, or invent evidence.
/// </summary>
public sealed class MovieProductionQualityControlService(
    IOptions<MovieProductionQualityControlOptions> options) : IMovieProductionQualityControl
{
    public const string ContractVersion = "movie-production-qc.v1";
    private readonly MovieProductionQualityControlOptions settings = options.Value;

    public MovieProductionQcDecision Evaluate(MovieProductionQcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Requirements);
        ArgumentNullException.ThrowIfNull(request.Evidence);

        var requirements = NormalizeRequirements(request.Requirements);
        var evidence = request.Evidence;
        var durationTolerance = requirements.DurationToleranceSeconds!.Value;
        var maxUpscaleFactor = requirements.MaxUpscaleFactor!.Value;
        var aspectRatioTolerance = requirements.AspectRatioTolerance!.Value;
        var maxContinuityWarnings = requirements.MaxContinuityWarnings!.Value;
        var findings = new List<MovieProductionQcFinding>();
        var reviewRequired = false;
        var hasValidCurrentResolution = evidence.CurrentResolution is { } observedResolution
            && IsValidResolution(observedResolution);

        if (!hasValidCurrentResolution)
        {
            Add(findings, MovieProductionQcReasonCodes.MeasurementMissing, "resolution", "error", "positive width and height", FormatResolution(evidence.CurrentResolution));
            reviewRequired = true;
        }
        if (evidence.DurationSeconds is null)
        {
            Add(findings, MovieProductionQcReasonCodes.DurationMissing, "duration", "error", requirements.ExpectedDurationSeconds.ToString(CultureInfo.InvariantCulture), null);
            reviewRequired = true;
        }
        else if (Math.Abs(evidence.DurationSeconds.Value - requirements.ExpectedDurationSeconds) > durationTolerance)
        {
            Add(findings, MovieProductionQcReasonCodes.DurationOutOfTolerance, "duration", "error", $"{requirements.ExpectedDurationSeconds}±{durationTolerance}s", evidence.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture));
            reviewRequired = true;
        }

        if (evidence.CurrentResolution is { } measuredResolution && IsValidResolution(measuredResolution))
        {
            var aspectDifference = Math.Abs(measuredResolution.AspectRatio - requirements.TargetResolution.AspectRatio);
            if (aspectDifference > aspectRatioTolerance)
            {
                Add(findings, MovieProductionQcReasonCodes.AspectRatioMismatch, "composition", "error", FormatAspectRatio(requirements.TargetResolution), FormatAspectRatio(measuredResolution));
                reviewRequired = true;
            }
        }

        if (requirements.RequireContinuityEvidence)
        {
            if (string.IsNullOrWhiteSpace(evidence.ContinuitySnapshotHash))
            {
                Add(findings, MovieProductionQcReasonCodes.ContinuityEvidenceMissing, "continuity", "error", "continuity snapshot hash", null);
                reviewRequired = true;
            }
            else if (!string.IsNullOrWhiteSpace(requirements.RequiredContinuitySnapshotHash)
                && !string.Equals(evidence.ContinuitySnapshotHash.Trim(), requirements.RequiredContinuitySnapshotHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Add(findings, MovieProductionQcReasonCodes.ContinuityHashMismatch, "continuity", "error", requirements.RequiredContinuitySnapshotHash, evidence.ContinuitySnapshotHash);
                reviewRequired = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(requirements.RequiredContinuitySnapshotHash)
            && !string.Equals(evidence.ContinuitySnapshotHash?.Trim(), requirements.RequiredContinuitySnapshotHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Add(findings, MovieProductionQcReasonCodes.ContinuityHashMismatch, "continuity", "error", requirements.RequiredContinuitySnapshotHash, evidence.ContinuitySnapshotHash);
            reviewRequired = true;
        }

        if (evidence.HardContinuityConflictCount < 0 || evidence.ContinuityWarningCount < 0)
        {
            Add(findings, MovieProductionQcReasonCodes.MeasurementInvalid, "continuity", "error", "non-negative warning counts", $"warnings={evidence.ContinuityWarningCount};hard={evidence.HardContinuityConflictCount}");
            reviewRequired = true;
        }
        else
        {
            if (evidence.HardContinuityConflictCount > 0)
            {
                Add(findings, MovieProductionQcReasonCodes.HardContinuityConflict, "continuity", "error", "0", evidence.HardContinuityConflictCount.ToString(CultureInfo.InvariantCulture));
                reviewRequired = true;
            }
            if (evidence.ContinuityWarningCount > maxContinuityWarnings)
            {
                Add(findings, MovieProductionQcReasonCodes.ContinuityWarningsExceeded, "continuity", "warning", $"≤{maxContinuityWarnings}", evidence.ContinuityWarningCount.ToString(CultureInfo.InvariantCulture));
                reviewRequired = true;
            }
        }

        if (reviewRequired)
            return Decision(MovieProductionQcActions.RequireReview, findings, evidence.CurrentResolution, requirements, null, null, true);

        var current = evidence.CurrentResolution!;
        if (current.Width >= requirements.TargetResolution.Width && current.Height >= requirements.TargetResolution.Height)
        {
            Add(findings, MovieProductionQcReasonCodes.Accepted, "resolution", "info", FormatResolution(requirements.TargetResolution), FormatResolution(current));
            return Decision(MovieProductionQcActions.Accept, findings, current, requirements, null, null, false);
        }

        var scale = current.ScaleRequiredFor(requirements.TargetResolution);
        if (current.Width >= requirements.MinimumSourceResolution.Width
            && current.Height >= requirements.MinimumSourceResolution.Height
            && scale <= maxUpscaleFactor)
        {
            Add(findings, MovieProductionQcReasonCodes.TargetResolutionUnmet, "resolution", "warning", FormatResolution(requirements.TargetResolution), FormatResolution(current));
            Add(findings, MovieProductionQcReasonCodes.UpscaleWithinBounds, "escalation", "info", $"scale≤{maxUpscaleFactor:0.##}x", $"scale={scale:0.##}x");
            return ApplyEconomics(
                MovieProductionQcActions.Upscale,
                findings,
                current,
                requirements,
                request.Economics,
                request.Economics?.EstimatedUpscaleCostUsd);
        }

        Add(findings, MovieProductionQcReasonCodes.TargetResolutionUnmet, "resolution", "warning", FormatResolution(requirements.TargetResolution), FormatResolution(current));
        Add(findings, MovieProductionQcReasonCodes.SourceResolutionInsufficient, "escalation", "error", FormatResolution(requirements.MinimumSourceResolution), FormatResolution(current));
        return ApplyEconomics(
            MovieProductionQcActions.RegenerateAtHigherSourceResolution,
            findings,
            current,
            requirements,
            request.Economics,
            request.Economics?.EstimatedRegenerationCostUsd);
    }

    private MovieProductionQcDecision ApplyEconomics(
        string action,
        List<MovieProductionQcFinding> findings,
        MovieResolution current,
        MovieProductionQcRequirements requirements,
        MovieProductionQcEconomics? economics,
        decimal? estimatedCost)
    {
        var requireKnownCost = economics?.RequireKnownEscalationCost ?? settings.RequireKnownEscalationCost;
        if (estimatedCost is < 0 || economics?.MaxAdditionalCostUsd is < 0)
        {
            Add(findings, MovieProductionQcReasonCodes.MeasurementInvalid, "economics", "error", "non-negative cost", FormatCost(estimatedCost));
            return Decision(MovieProductionQcActions.RequireReview, findings, current, requirements, null, estimatedCost, true);
        }
        if ((requireKnownCost || economics?.MaxAdditionalCostUsd is not null) && !estimatedCost.HasValue)
        {
            Add(findings, MovieProductionQcReasonCodes.EscalationCostUnknown, "economics", "warning", "known additional cost", null);
            return Decision(MovieProductionQcActions.RequireReview, findings, current, requirements, null, null, true);
        }
        if (economics?.MaxAdditionalCostUsd is decimal limit && estimatedCost is decimal cost && cost > limit)
        {
            Add(findings, MovieProductionQcReasonCodes.EscalationCostLimitExceeded, "economics", "warning", FormatCost(limit), FormatCost(cost));
            return Decision(MovieProductionQcActions.RequireReview, findings, current, requirements, null, cost, true);
        }
        return Decision(action, findings, current, requirements, action == MovieProductionQcActions.RegenerateAtHigherSourceResolution
            ? requirements.RecommendedRegenerationSourceResolution
            : requirements.TargetResolution, estimatedCost, false);
    }

    private MovieProductionQcRequirements NormalizeRequirements(MovieProductionQcRequirements requirements)
    {
        if (!IsValidResolution(requirements.TargetResolution) || !IsValidResolution(requirements.MinimumSourceResolution))
            throw new ArgumentException("QC target and minimum source resolutions must have positive bounded dimensions.", nameof(requirements));
        if (requirements.MinimumSourceResolution.Width > requirements.TargetResolution.Width
            || requirements.MinimumSourceResolution.Height > requirements.TargetResolution.Height)
            throw new ArgumentException("QC minimum source resolution cannot exceed the target resolution.", nameof(requirements));
        if (requirements.ExpectedDurationSeconds <= 0)
            throw new ArgumentException("QC expected duration must be positive.", nameof(requirements));
        var durationTolerance = requirements.DurationToleranceSeconds ?? settings.DefaultDurationToleranceSeconds;
        var maxUpscale = requirements.MaxUpscaleFactor ?? settings.DefaultMaxUpscaleFactor;
        var aspectTolerance = requirements.AspectRatioTolerance ?? settings.DefaultAspectRatioTolerance;
        var maxWarnings = requirements.MaxContinuityWarnings ?? settings.DefaultMaxContinuityWarnings;
        if (durationTolerance < 0 || maxUpscale < 1m || aspectTolerance < 0m || maxWarnings < 0)
            throw new ArgumentException("QC tolerances and warning limits must be non-negative; upscale factor must be at least 1.", nameof(requirements));
        if (requirements.RecommendedRegenerationSourceResolution is { } recommended
            && (!IsValidResolution(recommended)
                || recommended.Width < requirements.TargetResolution.Width
                || recommended.Height < requirements.TargetResolution.Height))
            throw new ArgumentException("QC recommended regeneration resolution must be bounded and meet the target resolution.", nameof(requirements));
        return requirements with
        {
            DurationToleranceSeconds = durationTolerance,
            MaxUpscaleFactor = maxUpscale,
            AspectRatioTolerance = aspectTolerance,
            MaxContinuityWarnings = maxWarnings,
            RecommendedRegenerationSourceResolution = requirements.RecommendedRegenerationSourceResolution ?? requirements.TargetResolution,
        };
    }

    private MovieProductionQcDecision Decision(
        string action,
        List<MovieProductionQcFinding> findings,
        MovieResolution? current,
        MovieProductionQcRequirements requirements,
        MovieResolution? recommendedSourceResolution,
        decimal? estimatedCost,
        bool requiresReview)
    {
        var bounded = findings
            .Take(Math.Clamp(settings.MaxFindings, 1, 64))
            .ToArray();
        return new(
            ContractVersion,
            action,
            requiresReview,
            bounded,
            current,
            requirements.TargetResolution,
            recommendedSourceResolution,
            estimatedCost);
    }

    private static bool HasPositiveResolution(MovieResolution? resolution) =>
        resolution is { Width: > 0, Height: > 0 };

    private bool IsValidResolution(MovieResolution resolution) =>
        HasPositiveResolution(resolution)
        && resolution.Width <= Math.Clamp(settings.MaxResolutionDimension, 1, 65_535)
        && resolution.Height <= Math.Clamp(settings.MaxResolutionDimension, 1, 65_535);

    private static string? FormatResolution(MovieResolution? resolution) =>
        resolution is null ? null : $"{resolution.Width}x{resolution.Height}";

    private static string FormatAspectRatio(MovieResolution resolution) =>
        $"{resolution.Width}:{resolution.Height}";

    private static string? FormatCost(decimal? cost) =>
        cost?.ToString("0.########", CultureInfo.InvariantCulture);

    private static void Add(List<MovieProductionQcFinding> findings, string reasonCode, string category, string severity, string? expected, string? actual) =>
        findings.Add(new(reasonCode, category, severity, expected, actual));
}

/// <summary>
/// Reads only the provider-neutral media measurements emitted by a movie pipeline. Missing or
/// malformed fields become missing evidence and are handled by the evaluator as review-required.
/// </summary>
public static class MovieProductionQcEvidenceParser
{
    public static MovieProductionQcEvidence Parse(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new(null, null, null);
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new(null, null, null);
            var root = document.RootElement;
            var width = PositiveInt(root, "width");
            var height = PositiveInt(root, "height");
            var resolution = width.HasValue && height.HasValue ? new MovieResolution(width.Value, height.Value) : null;
            var duration = PositiveInt(root, "durationSeconds");
            var continuityHash = StringValue(root, "continuitySnapshotHash");
            var warnings = Count(root, "continuityWarningCount");
            var hardConflicts = Count(root, "hardContinuityConflictCount");
            var outputHash = StringValue(root, "outputSha256");
            return new(resolution, duration, continuityHash, warnings, hardConflicts, outputHash);
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
    }

    private static int? PositiveInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetInt32(out var value) || value <= 0) return null;
        return value;
    }

    private static int Count(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)) return 0;
        return property.TryGetInt32(out var value) && value >= 0 ? value : -1;
    }

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? Bounded(property.GetString()?.Trim())
            : null;

    private static string? Bounded(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 ? value : null;
}

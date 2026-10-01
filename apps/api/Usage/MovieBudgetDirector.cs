using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public static class MovieBudgetDirectorStates
{
    public const string Estimated = "estimated";
    public const string Unknown = "unknown";
    public const string Unevaluated = "unevaluated";
}

public static class MovieBudgetDirectorReasons
{
    public const string RequestInvalid = "request_invalid";
    public const string CostEstimateUnavailable = "cost_estimate_unavailable";
    public const string LedgerEvidenceRequired = "ledger_evidence_required";
    public const string NoGenerationUnits = "no_generation_units";
}

/// <summary>
/// Planning input only. Duration is the representative duration of one shot, not
/// a customer charge or a provider request. Provider and model selection remain
/// server-side concerns of the existing cost estimator.
/// </summary>
public sealed class MovieBudgetDirectorRequest
{
    public int DurationSeconds { get; set; } = 5;
    public int ShotCount { get; set; } = 1;
    public int CandidatePassesPerShot { get; set; } = 3;
    public int ApprovedReferenceReuseCount { get; set; }
    public int SalvagedSelectCount { get; set; }
    public int MissingInsertCount { get; set; }
    public int SelectedTakeCount { get; set; }
    public int MissingInsertDurationSeconds { get; set; } = 2;
    public bool SelectedOnlyMastering { get; set; } = true;
    public string NaiveSourceResolution { get; set; } = "1080p";
    public string NaiveTargetResolution { get; set; } = "2160p";
    public string NaiveQualityTier { get; set; } = MovieQualityLevels.Studio;
    public string DraftSourceResolution { get; set; } = "480p";
    public string DraftTargetResolution { get; set; } = "480p";
    public string DraftQualityTier { get; set; } = MovieQualityLevels.Fast;
    public string MasterSourceResolution { get; set; } = "1080p";
    public string MasterTargetResolution { get; set; } = "2160p";
    public string MasterQualityTier { get; set; } = MovieQualityLevels.Studio;
}

public sealed record MovieBudgetDirectorPlanItem(
    string Key,
    string Label,
    int GenerationUnits,
    int DurationSecondsPerUnit,
    string Rationale);

/// <summary>
/// A scenario is intentionally a safe estimate range. It contains no provider,
/// model, prompt, endpoint, credential, or customer-charge fields.
/// </summary>
public sealed record MovieBudgetDirectorScenario(
    string Key,
    string Label,
    string Description,
    string State,
    decimal? MinimumAmountUsd,
    decimal? MaximumAmountUsd,
    string Currency,
    int GenerationUnits,
    IReadOnlyList<MovieGenerationCostComponent> Components,
    string? Reason = null);

public sealed record MovieBudgetDirectorSavings(
    string State,
    decimal? MinimumAmountUsd,
    decimal? MaximumAmountUsd,
    decimal? MinimumPercent,
    decimal? MaximumPercent,
    bool IsEstimate,
    bool ActualSavingsAvailable,
    decimal? ActualSavingsUsd,
    string Basis,
    string? Reason = null);

public sealed record MovieBudgetDirectorEstimate(
    string State,
    string Currency,
    MovieBudgetDirectorScenario NaivePath,
    MovieBudgetDirectorScenario OptimizedPath,
    MovieBudgetDirectorSavings Savings,
    IReadOnlyList<MovieBudgetDirectorPlanItem> Plan,
    IReadOnlyList<string> Assumptions,
    DateTime GeneratedAtUtc)
{
    public bool IsEstimated => string.Equals(State, MovieBudgetDirectorStates.Estimated, StringComparison.Ordinal);
}

public interface IMovieBudgetDirectorService
{
    Task<MovieBudgetDirectorEstimate> EstimateAsync(MovieBudgetDirectorRequest request, CancellationToken cancellationToken = default);
    Task<MovieBudgetDirectorEstimate?> EstimateProjectAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken = default);
    Task<MovieBudgetDirectorEstimate?> EstimateProjectAsync(Guid userId, Guid movieProjectId, MovieBudgetDirectorRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Compares a deliberately expensive whole-shot path with a reversible draft,
/// salvage, insert, and selected-only mastering path. This service never queues
/// generation, changes a take, writes a ledger entry, or claims realized savings.
/// </summary>
public sealed class MovieBudgetDirectorService(
    TaslimDbContext db,
    IMovieGenerationCostEstimator costEstimator,
    MovieCollaborationAccess collaboration) : IMovieBudgetDirectorService
{
    private const string NaivePathKey = "naive_high_cost";
    private const string OptimizedPathKey = "optimized_draft_select_master";

    public async Task<MovieBudgetDirectorEstimate> EstimateAsync(
        MovieBudgetDirectorRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var draftShots = Math.Max(0, request.ShotCount - request.ApprovedReferenceReuseCount - request.SalvagedSelectCount);
        var selectedMasterShots = request.SelectedOnlyMastering
            ? request.SelectedTakeCount
            : request.ShotCount;
        var naiveUnits = checked(request.ShotCount * request.CandidatePassesPerShot);

        var naiveEstimate = await EstimateRepeatedAsync(
            new MovieGenerationCostRequest(
                request.DurationSeconds,
                request.NaiveSourceResolution,
                request.NaiveTargetResolution,
                request.NaiveQualityTier,
                "native_high_quality_then_4k_master",
                RetryAttempts: 0,
                UpscalingRequested: true,
                UpscalePasses: 1),
            naiveUnits,
            request.DurationSeconds,
            cancellationToken);

        var optimizedParts = new List<MovieGenerationCostEstimate>();
        var plan = new List<MovieBudgetDirectorPlanItem>();
        if (draftShots > 0)
        {
            var draft = await EstimateRepeatedAsync(
                new MovieGenerationCostRequest(
                    request.DurationSeconds,
                    request.DraftSourceResolution,
                    request.DraftTargetResolution,
                    request.DraftQualityTier,
                    "native",
                    RetryAttempts: 0,
                    UpscalingRequested: false,
                    UpscalePasses: 0),
                checked(draftShots * request.CandidatePassesPerShot),
                request.DurationSeconds,
                cancellationToken);
            optimizedParts.Add(draft);
            plan.Add(new MovieBudgetDirectorPlanItem(
                "low_resolution_drafts",
                "Low-resolution draft passes",
                checked(draftShots * request.CandidatePassesPerShot),
                request.DurationSeconds,
                "Review rhythm and framing cheaply before committing to a finish pass."));
        }
        if (request.MissingInsertCount > 0)
        {
            var inserts = await EstimateRepeatedAsync(
                new MovieGenerationCostRequest(
                    request.MissingInsertDurationSeconds,
                    request.DraftSourceResolution,
                    request.DraftTargetResolution,
                    request.DraftQualityTier,
                    "native",
                    RetryAttempts: 0,
                    UpscalingRequested: false,
                    UpscalePasses: 0),
                request.MissingInsertCount,
                request.MissingInsertDurationSeconds,
                cancellationToken);
            optimizedParts.Add(inserts);
            plan.Add(new MovieBudgetDirectorPlanItem(
                "targeted_inserts",
                "Targeted missing inserts",
                request.MissingInsertCount,
                request.MissingInsertDurationSeconds,
                "Generate only the missing coverage instead of regenerating a whole scene."));
        }
        if (selectedMasterShots > 0)
        {
            var master = await EstimateRepeatedAsync(
                new MovieGenerationCostRequest(
                    request.DurationSeconds,
                    request.MasterSourceResolution,
                    request.MasterTargetResolution,
                    request.MasterQualityTier,
                    "native_high_quality_then_4k_master",
                    RetryAttempts: 0,
                    UpscalingRequested: true,
                    UpscalePasses: 1),
                selectedMasterShots,
                request.DurationSeconds,
                cancellationToken);
            optimizedParts.Add(master);
            plan.Add(new MovieBudgetDirectorPlanItem(
                "selected_only_mastering",
                "Selected-take mastering",
                selectedMasterShots,
                request.DurationSeconds,
                request.SelectedOnlyMastering
                    ? "Reserve the finish pass for explicitly selected takes only."
                    : "Mastering is planned for every shot because selected-only mastering was disabled."));
        }
        if (request.ApprovedReferenceReuseCount > 0)
        {
            plan.Add(new MovieBudgetDirectorPlanItem(
                "approved_reference_reuse",
                "Approved reference reuse",
                request.ApprovedReferenceReuseCount,
                0,
                "Reuse locked character, location, prop, and spatial references instead of rebuilding them."));
        }
        if (request.SalvagedSelectCount > 0)
        {
            plan.Add(new MovieBudgetDirectorPlanItem(
                "salvaged_selects",
                "Salvaged usable ranges",
                request.SalvagedSelectCount,
                0,
                "Keep usable ranges from raw footage and regenerate only what is missing."));
        }

        var optimizedEstimate = Combine(optimizedParts, OptimizedPathKey, "Optimized draft/select/master path", "Draft cheaply, salvage usable footage, fill inserts, and master selected material only.");
        var naiveScenario = ToScenario(NaivePathKey, "Naive high-cost path", "Whole-shot high-finish generation for every candidate pass.", naiveEstimate, naiveUnits);
        var optimizedUnits = plan.Where(item => item.DurationSecondsPerUnit > 0).Sum(item => item.GenerationUnits);
        var optimizedScenario = ToScenario(OptimizedPathKey, "Optimized draft/select/master path", "Low-cost drafts with continuity reuse, salvage, targeted inserts, and selected-only mastering.", optimizedEstimate, optimizedUnits);
        var savings = BuildSavings(naiveEstimate, optimizedEstimate, naiveScenario, optimizedScenario);
        var state = savings.State;
        var currency = ResolveCurrency(naiveEstimate, optimizedEstimate);
        var assumptions = new List<string>
        {
            "All amounts are estimates from the existing persisted capability pricing catalog; they are not charges.",
            "Generated footage is treated as raw footage: usable ranges are retained before a regeneration decision.",
            "Reference reuse reduces planned draft work only when the count is explicitly supplied or derived from persisted project state.",
            request.SelectedOnlyMastering
                ? "Final mastering is modeled for selected takes only."
                : "Selected-only mastering is disabled for this preview; the optimized path may overstate finishing work.",
            "Actual savings remain unavailable until completed ledger evidence compares realized work; this preview never infers them.",
        };

        return new MovieBudgetDirectorEstimate(
            state,
            currency,
            naiveScenario,
            optimizedScenario,
            savings,
            plan,
            assumptions,
            DateTime.UtcNow);
    }

    public async Task<MovieBudgetDirectorEstimate?> EstimateProjectAsync(
        Guid userId,
        Guid movieProjectId,
        CancellationToken cancellationToken = default)
    {
        return await EstimateProjectAsync(userId, movieProjectId, null, cancellationToken);
    }

    public async Task<MovieBudgetDirectorEstimate?> EstimateProjectAsync(
        Guid userId,
        Guid movieProjectId,
        MovieBudgetDirectorRequest? request,
        CancellationToken cancellationToken = default)
    {
        var project = await db.MovieProjects.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        var shots = await db.MovieShots.AsNoTracking()
            .Where(item => item.Scene.MovieProjectId == movieProjectId && item.ArchivedAt == null)
            .Select(item => new { item.DurationSeconds, item.SelectedTakeId, item.FinalTakeId })
            .ToListAsync(cancellationToken);
        var shotCount = shots.Count;
        var duration = shotCount == 0
            ? Math.Max(1, project.DurationSeconds)
            : Math.Max(1, (int)Math.Ceiling(shots.Sum(item => item.DurationSeconds ?? 0) > 0
                ? shots.Sum(item => item.DurationSeconds ?? 0) / (double)shotCount
                : Math.Max(1, project.DurationSeconds) / (double)shotCount));
        var selectedTakeCount = shots.Count(item => item.SelectedTakeId.HasValue || item.FinalTakeId.HasValue);
        var derivedRequest = request ?? new MovieBudgetDirectorRequest
        {
            DurationSeconds = duration,
            ShotCount = Math.Max(shotCount, 1),
            CandidatePassesPerShot = 3,
            SelectedTakeCount = selectedTakeCount,
            SelectedOnlyMastering = true,
        };
        return await EstimateAsync(derivedRequest, cancellationToken);
    }

    private async Task<MovieGenerationCostEstimate> EstimateRepeatedAsync(
        MovieGenerationCostRequest request,
        int units,
        int durationSecondsPerUnit,
        CancellationToken cancellationToken)
    {
        if (units <= 0) return NoGenerationEstimate();
        var estimate = await costEstimator.EstimateAsync(request with { DurationSeconds = durationSecondsPerUnit }, cancellationToken: cancellationToken);
        return Scale(estimate, units);
    }

    private static MovieGenerationCostEstimate Combine(
        IReadOnlyList<MovieGenerationCostEstimate> estimates,
        string key,
        string label,
        string description)
    {
        if (estimates.Count == 0) return NoGenerationEstimate();
        if (estimates.Any(item => item.State == MovieGenerationCostEstimateStates.Unknown))
            return MovieGenerationCostEstimate.Unknown(MovieBudgetDirectorReasons.CostEstimateUnavailable);
        if (estimates.Any(item => item.State == MovieGenerationCostEstimateStates.Unevaluated))
            return MovieGenerationCostEstimate.Unevaluated(MovieBudgetDirectorReasons.CostEstimateUnavailable);
        var currencies = estimates.Select(item => item.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (currencies.Length != 1) return MovieGenerationCostEstimate.Unknown(MovieGenerationCostEstimateReasons.CurrencyMismatch);
        var components = estimates.SelectMany(item => item.Components)
            .GroupBy(item => $"{item.Dimension}\u001f{item.Unit}", StringComparer.Ordinal)
            .Select(group => new MovieGenerationCostComponent(
                group.First().Dimension,
                group.Sum(item => item.Quantity),
                group.First().Unit,
                RoundNullable(group.Sum(item => item.MinimumUsd ?? 0m)),
                RoundNullable(group.Sum(item => item.MaximumUsd ?? 0m))))
            .ToArray();
        return new MovieGenerationCostEstimate(
            MovieGenerationCostEstimateStates.Estimated,
            RoundNullable(estimates.Sum(item => item.MinimumAmountUsd ?? 0m)),
            RoundNullable(estimates.Sum(item => item.MaximumAmountUsd ?? 0m)),
            currencies[0],
            null,
            null,
            null,
            components,
            null);
    }

    private static MovieBudgetDirectorSavings BuildSavings(
        MovieGenerationCostEstimate naive,
        MovieGenerationCostEstimate optimized,
        MovieBudgetDirectorScenario naiveScenario,
        MovieBudgetDirectorScenario optimizedScenario)
    {
        if (!naive.IsEstimated || !optimized.IsEstimated)
        {
            var state = naive.State == MovieGenerationCostEstimateStates.Unknown || optimized.State == MovieGenerationCostEstimateStates.Unknown
                ? MovieBudgetDirectorStates.Unknown
                : MovieBudgetDirectorStates.Unevaluated;
            return new MovieBudgetDirectorSavings(
                state,
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                "Estimated savings are unavailable until both paths have complete pricing.",
                MovieBudgetDirectorReasons.CostEstimateUnavailable);
        }
        var minimum = Math.Max(0m, (naive.MinimumAmountUsd ?? 0m) - (optimized.MaximumAmountUsd ?? 0m));
        var maximum = Math.Max(0m, (naive.MaximumAmountUsd ?? 0m) - (optimized.MinimumAmountUsd ?? 0m));
        var naiveMinimum = naive.MinimumAmountUsd ?? 0m;
        var naiveMaximum = naive.MaximumAmountUsd ?? 0m;
        var minimumPercent = naiveMaximum <= 0m ? 0m : Round(minimum / naiveMaximum * 100m);
        var maximumPercent = naiveMinimum <= 0m ? 0m : Round(maximum / naiveMinimum * 100m);
        return new MovieBudgetDirectorSavings(
            MovieBudgetDirectorStates.Estimated,
            Round(minimum),
            Round(maximum),
            Round(minimumPercent),
            Round(maximumPercent),
            true,
            false,
            null,
            "Estimated avoided provider cost range; not actual savings. Actual savings require completed ledger evidence.",
            MovieBudgetDirectorReasons.LedgerEvidenceRequired);
    }

    private static MovieBudgetDirectorScenario ToScenario(
        string key,
        string label,
        string description,
        MovieGenerationCostEstimate estimate,
        int units) => new(
            key,
            label,
            description,
            estimate.State,
            estimate.MinimumAmountUsd,
            estimate.MaximumAmountUsd,
            estimate.Currency,
            units,
            estimate.Components,
            estimate.Reason);

    private static MovieGenerationCostEstimate Scale(MovieGenerationCostEstimate estimate, int units)
    {
        if (units <= 0) return NoGenerationEstimate();
        if (!estimate.IsEstimated)
        {
            return estimate with { Components = estimate.Components.Select(item => item with { Quantity = item.Quantity * units }).ToArray() };
        }
        return estimate with
        {
            MinimumAmountUsd = RoundNullable((estimate.MinimumAmountUsd ?? 0m) * units),
            MaximumAmountUsd = RoundNullable((estimate.MaximumAmountUsd ?? 0m) * units),
            Components = estimate.Components.Select(item => item with
            {
                Quantity = item.Quantity * units,
                MinimumUsd = MultiplyNullable(item.MinimumUsd, units),
                MaximumUsd = MultiplyNullable(item.MaximumUsd, units),
            }).ToArray(),
        };
    }

    private static MovieGenerationCostEstimate NoGenerationEstimate() => new(
        MovieGenerationCostEstimateStates.Estimated,
        0m,
        0m,
        UsageCurrencies.Usd,
        null,
        null,
        null,
        [],
        MovieBudgetDirectorReasons.NoGenerationUnits);

    private static string ResolveCurrency(params MovieGenerationCostEstimate[] estimates) =>
        estimates.Select(item => item.Currency).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? UsageCurrencies.Usd;

    private static void Validate(MovieBudgetDirectorRequest request)
    {
        if (request.DurationSeconds is < 1 or > 3_600
            || request.ShotCount is < 1 or > 10_000
            || request.CandidatePassesPerShot is < 1 or > 32
            || request.ApprovedReferenceReuseCount < 0
            || request.SalvagedSelectCount < 0
            || request.MissingInsertCount < 0
            || request.SelectedTakeCount < 0
            || request.MissingInsertDurationSeconds is < 1 or > 3_600
            || request.ApprovedReferenceReuseCount > request.ShotCount
            || request.SalvagedSelectCount > request.ShotCount
            || request.SelectedTakeCount > request.ShotCount
            || request.MissingInsertCount > 10_000
            || string.IsNullOrWhiteSpace(request.NaiveSourceResolution)
            || string.IsNullOrWhiteSpace(request.NaiveTargetResolution)
            || string.IsNullOrWhiteSpace(request.NaiveQualityTier)
            || string.IsNullOrWhiteSpace(request.DraftSourceResolution)
            || string.IsNullOrWhiteSpace(request.DraftTargetResolution)
            || string.IsNullOrWhiteSpace(request.DraftQualityTier)
            || string.IsNullOrWhiteSpace(request.MasterSourceResolution)
            || string.IsNullOrWhiteSpace(request.MasterTargetResolution)
            || string.IsNullOrWhiteSpace(request.MasterQualityTier))
            throw new MovieBudgetDirectorValidationException(MovieBudgetDirectorReasons.RequestInvalid, "Choose valid shot, duration, and workflow planning values.");
    }

    private static decimal? MultiplyNullable(decimal? value, int factor) => value.HasValue ? RoundNullable(value.Value * factor) : null;
    private static decimal? RoundNullable(decimal? value) => value.HasValue ? Round(value.Value) : null;
    private static decimal Round(decimal value) => decimal.Round(Math.Max(0m, value), 8, MidpointRounding.AwayFromZero);
}

public sealed class MovieBudgetDirectorValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

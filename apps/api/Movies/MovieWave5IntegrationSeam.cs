using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Taslim.Api.Movies;

/// <summary>
/// Provider-neutral integration seam for the Movie Studio Wave5 intelligence pass.
/// It intentionally contains no persistence, provider, prompt, or charging behavior.
/// Final Wave5 feature branches can map their durable contracts to these bounded
/// decisions without changing the Wave2-4 production graph.
/// </summary>
public static class MovieWave5IntegrationContract
{
    public const string Version = "movie-production-intelligence.v1";
    public const string MinimalInsertScope = "minimal_insert";
    public const string SalvageScope = "salvage_existing_ranges";
    public const string NoRegenerationScope = "no_regeneration";

    public static MovieWave5PreflightResult EvaluatePreflight(
        Guid movieProjectId,
        Guid shotId,
        string? productionPackageHash,
        bool referencesLocked,
        bool shotReady,
        bool providerReady,
        bool budgetAllowsDraft)
    {
        var blockers = new List<string>();
        if (movieProjectId == Guid.Empty) blockers.Add("project_required");
        if (shotId == Guid.Empty) blockers.Add("shot_required");
        if (string.IsNullOrWhiteSpace(productionPackageHash)) blockers.Add("production_references_required");
        if (!referencesLocked) blockers.Add("production_references_must_be_locked");
        if (!shotReady) blockers.Add("shot_must_be_ready");
        if (!providerReady) blockers.Add("provider_unavailable");
        if (!budgetAllowsDraft) blockers.Add("draft_budget_blocked");

        return new MovieWave5PreflightResult(
            movieProjectId,
            shotId,
            productionPackageHash?.Trim() ?? string.Empty,
            blockers.Count == 0,
            blockers);
    }

    public static MovieWave5SalvagePlan BuildSalvagePlan(
        Guid shotId,
        IReadOnlyList<MovieWave5SegmentCandidate> candidates,
        decimal targetDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (shotId == Guid.Empty) throw new ArgumentException("A shot id is required.", nameof(shotId));
        if (targetDurationSeconds <= 0m) throw new ArgumentOutOfRangeException(nameof(targetDurationSeconds));

        var selected = candidates
            .Where(item => item.IsUsable && item.DurationSeconds > 0m)
            .OrderBy(item => item.StartSeconds)
            .ThenBy(item => item.TakeId)
            .ThenBy(item => item.SegmentId, StringComparer.Ordinal)
            .ToArray();
        var bounded = new List<MovieWave5SegmentSelection>();
        var cursor = 0m;
        foreach (var candidate in selected)
        {
            if (cursor >= targetDurationSeconds) break;
            var start = Math.Max(cursor, candidate.StartSeconds);
            var end = Math.Min(targetDurationSeconds, candidate.StartSeconds + candidate.DurationSeconds);
            if (end <= start) continue;
            bounded.Add(new MovieWave5SegmentSelection(candidate.TakeId, candidate.SegmentId, start, end - start, candidate.ContinuityKey, candidate.ScreenDirection));
            cursor = Math.Max(cursor, end);
        }

        var inserts = new List<MovieWave5InsertProposal>();
        var coverageCursor = 0m;
        foreach (var segment in bounded.OrderBy(item => item.StartSeconds))
        {
            if (segment.StartSeconds > coverageCursor)
                inserts.Add(Insert(shotId, coverageCursor, segment.StartSeconds - coverageCursor, bounded));
            coverageCursor = Math.Max(coverageCursor, segment.StartSeconds + segment.DurationSeconds);
        }
        if (coverageCursor < targetDurationSeconds)
            inserts.Add(Insert(shotId, coverageCursor, targetDurationSeconds - coverageCursor, bounded));

        return new MovieWave5SalvagePlan(
            shotId,
            targetDurationSeconds,
            bounded,
            inserts,
            inserts.Count == 0 ? NoRegenerationScope : MinimalInsertScope,
            inserts.Count == 0
                ? "Usable ranges cover the target; preserve them before considering any regeneration."
                : "Only uncovered ranges require inserts; do not regenerate the complete scene.");
    }

    public static MovieWave5ContinuityResult CheckContinuity(
        IReadOnlyList<MovieWave5SegmentSelection> segments,
        string expectedContinuityKey,
        string expectedScreenDirection)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var normalizedKey = expectedContinuityKey?.Trim() ?? string.Empty;
        var normalizedDirection = expectedScreenDirection?.Trim() ?? string.Empty;
        if (segments.Count == 0)
            return new(false, "segments_required", "No selected segment can establish continuity.");
        if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(normalizedDirection))
            return new(false, "continuity_anchor_required", "Character, location, prop, and screen-direction anchors are required.");
        if (segments.Any(item => !string.Equals(item.ContinuityKey, normalizedKey, StringComparison.Ordinal)
            || !string.Equals(item.ScreenDirection, normalizedDirection, StringComparison.Ordinal)))
            return new(false, "continuity_or_screen_direction_conflict", "Selected ranges do not share the locked continuity and screen-direction anchors.");
        return new(true, null, "Selected ranges preserve the locked continuity and screen direction.");
    }

    public static MovieWave5BudgetPlan OptimizeDraftFirst(MovieWave5BudgetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = input.BudgetConstrained ? MovieResolutionTiers.P720 : NormalizeResolution(input.PreferredSourceResolution);
        var master = NormalizeResolution(input.TargetMasterResolution);
        return new MovieWave5BudgetPlan(
            source,
            master,
            UpgradeSelectedOnly: true,
            input.EstimatedCostUsd,
            input.EstimatedCostUsd.HasValue
                ? "Use the economical source for review and reserve the upgrade/master pass for the explicitly selected final take."
                : "Provider cost is unknown; preserve the economical draft-first path and do not invent a charge.");
    }

    public static string ProvenanceHash(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static MovieWave5InsertProposal Insert(
        Guid shotId,
        decimal startSeconds,
        decimal durationSeconds,
        IReadOnlyList<MovieWave5SegmentSelection> selected)
    {
        var continuity = selected.LastOrDefault();
        return new MovieWave5InsertProposal(
            shotId,
            startSeconds,
            durationSeconds,
            "Generate only the missing narrative insert needed to bridge the selected usable ranges.",
            continuity?.ContinuityKey ?? "locked-shot-context",
            continuity?.ScreenDirection ?? "locked-screen-direction",
            IsMinimal: true);
    }

    private static string NormalizeResolution(string? value) =>
        string.IsNullOrWhiteSpace(value) ? MovieResolutionTiers.P1080 : value.Trim().ToLowerInvariant();
}

public sealed record MovieWave5PreflightResult(
    Guid MovieProjectId,
    Guid ShotId,
    string ProductionPackageHash,
    bool Ready,
    IReadOnlyList<string> BlockingReasons);

public sealed record MovieWave5SegmentCandidate(
    Guid TakeId,
    string SegmentId,
    decimal StartSeconds,
    decimal DurationSeconds,
    bool IsUsable,
    string ContinuityKey,
    string ScreenDirection);

public sealed record MovieWave5SegmentSelection(
    Guid TakeId,
    string SegmentId,
    decimal StartSeconds,
    decimal DurationSeconds,
    string ContinuityKey,
    string ScreenDirection);

public sealed record MovieWave5InsertProposal(
    Guid ShotId,
    decimal StartSeconds,
    decimal DurationSeconds,
    string NarrativePurpose,
    string ContinuityAnchor,
    string ScreenDirection,
    bool IsMinimal);

public sealed record MovieWave5SalvagePlan(
    Guid ShotId,
    decimal TargetDurationSeconds,
    IReadOnlyList<MovieWave5SegmentSelection> SelectedSegments,
    IReadOnlyList<MovieWave5InsertProposal> Inserts,
    string RegenerationScope,
    string Rationale)
{
    public bool RequiresRegeneration => Inserts.Count > 0;
}

public sealed record MovieWave5ContinuityResult(bool Passed, string? FailureCode, string Message);

public sealed record MovieWave5BudgetInput(
    string TargetMasterResolution,
    bool BudgetConstrained,
    string? PreferredSourceResolution = null,
    decimal? EstimatedCostUsd = null);

public sealed record MovieWave5BudgetPlan(
    string DraftSourceResolution,
    string TargetMasterResolution,
    bool UpgradeSelectedOnly,
    decimal? EstimatedCostUsd,
    string Rationale);

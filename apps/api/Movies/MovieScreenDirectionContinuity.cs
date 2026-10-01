using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taslim.Api.Movies;

/// <summary>
/// Optional, persisted screen-direction intent for one planned shot. Null/empty values
/// preserve legacy shots and mean that the director has not locked that dimension yet.
/// Entrance/exit directions are screen sides; a continuing subject normally exits one
/// side and enters the opposite side in the next shot while staying on the same axis.
/// </summary>
public sealed record MovieScreenDirectionPlan(
    string? Axis = null,
    string? Orientation = null,
    string? EntranceDirection = null,
    string? ExitDirection = null,
    string? EyelineDirection = null,
    string? EyelineTarget = null,
    string? SpatialAnchor = null,
    bool AxisBreak = false,
    string? Notes = null,
    int SchemaVersion = 1);

public static class MovieScreenDirectionValues
{
    public const string ScreenLeft = "screen_left";
    public const string ScreenRight = "screen_right";
    public const string Center = "center";
    public const string Foreground = "foreground";
    public const string Background = "background";
    public const string Neutral = "neutral";
    public const string SideA = "side_a";
    public const string SideB = "side_b";
    public const string AxisBreak = "axis_break";

    public static readonly IReadOnlySet<string> Directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ScreenLeft, ScreenRight, Center, Foreground, Background, Neutral,
    };

    public static readonly IReadOnlySet<string> Orientations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SideA, SideB, Neutral, AxisBreak,
    };
}

public static class MovieScreenDirectionPlanValidator
{
    public const int MaxAxisLength = 120;
    public const int MaxLabelLength = 120;
    public const int MaxTargetLength = 240;
    public const int MaxAnchorLength = 240;
    public const int MaxNotesLength = 2_000;

    public static string? Validate(MovieScreenDirectionPlan? plan)
    {
        if (plan is null) return null;
        if (plan.SchemaVersion != 1) return "Screen-direction plan schema version is not supported.";
        if (!ValidLength(plan.Axis, MaxAxisLength) || !ValidLength(plan.Orientation, MaxLabelLength) ||
            !ValidLength(plan.EntranceDirection, MaxLabelLength) || !ValidLength(plan.ExitDirection, MaxLabelLength) ||
            !ValidLength(plan.EyelineDirection, MaxLabelLength) || !ValidLength(plan.EyelineTarget, MaxTargetLength) ||
            !ValidLength(plan.SpatialAnchor, MaxAnchorLength) || !ValidLength(plan.Notes, MaxNotesLength))
            return "Screen-direction plan contains a field that is too long.";
        if (!Allowed(plan.EntranceDirection, MovieScreenDirectionValues.Directions) || !Allowed(plan.ExitDirection, MovieScreenDirectionValues.Directions) || !Allowed(plan.EyelineDirection, MovieScreenDirectionValues.Directions))
            return "Entrance, exit, and eyeline direction must use a supported screen direction.";
        if (!Allowed(plan.Orientation, MovieScreenDirectionValues.Orientations))
            return "Screen orientation must be side_a, side_b, neutral, or axis_break.";
        return null;
    }

    public static MovieScreenDirectionPlan? Normalize(MovieScreenDirectionPlan? plan)
    {
        if (plan is null) return null;
        var normalized = new MovieScreenDirectionPlan(
            Clean(plan.Axis, MaxAxisLength),
            CleanChoice(plan.Orientation),
            CleanChoice(plan.EntranceDirection),
            CleanChoice(plan.ExitDirection),
            CleanChoice(plan.EyelineDirection),
            Clean(plan.EyelineTarget, MaxTargetLength),
            Clean(plan.SpatialAnchor, MaxAnchorLength),
            plan.AxisBreak || string.Equals(plan.Orientation, MovieScreenDirectionValues.AxisBreak, StringComparison.OrdinalIgnoreCase),
            Clean(plan.Notes, MaxNotesLength),
            1);
        return HasContent(normalized) ? normalized : null;
    }

    private static bool Allowed(string? value, IReadOnlySet<string> allowed) => string.IsNullOrWhiteSpace(value) || allowed.Contains(value.Trim());
    private static bool ValidLength(string? value, int max) => value is null || value.Length <= max;
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? CleanChoice(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static bool HasContent(MovieScreenDirectionPlan plan) => plan.AxisBreak || plan.Axis is not null || plan.Orientation is not null || plan.EntranceDirection is not null || plan.ExitDirection is not null || plan.EyelineDirection is not null || plan.EyelineTarget is not null || plan.SpatialAnchor is not null || plan.Notes is not null;
}

public static class MovieScreenDirectionPlanCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? ToJson(MovieScreenDirectionPlan? plan)
    {
        var normalized = MovieScreenDirectionPlanValidator.Normalize(plan);
        return normalized is null ? null : JsonSerializer.Serialize(normalized, Options);
    }

    public static MovieScreenDirectionPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<MovieScreenDirectionPlan>(json, Options);
            return MovieScreenDirectionPlanValidator.Normalize(plan);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record MovieScreenDirectionRecommendationDto(
    string Kind,
    Guid TargetShotId,
    Guid? ReferenceShotId,
    string Reason,
    string SafeAction,
    bool AutomaticRewrite = false);

public sealed record MovieScreenDirectionReviewResult(
    IReadOnlyList<DirectorStoryFindingDto> Findings,
    IReadOnlyList<MovieScreenDirectionRecommendationDto> Recommendations);

public static class MovieScreenDirectionAnalyzer
{
    public static MovieScreenDirectionReviewResult Analyze(MovieProductionContinuityContext context)
    {
        var findings = new List<DirectorStoryFindingDto>();
        var recommendations = new List<MovieScreenDirectionRecommendationDto>();
        foreach (var scene in context.Scenes)
        {
            var shots = scene.Shots.OrderBy(item => item.Sequence).ThenBy(item => item.Id).ToArray();
            for (var index = 1; index < shots.Length; index++)
            {
                var previous = shots[index - 1];
                var current = shots[index];
                if (context.ShotId.HasValue && context.ShotId != previous.Id && context.ShotId != current.Id) continue;
                ComparePair(scene, previous, current, context, findings, recommendations);
            }
        }
        return new MovieScreenDirectionReviewResult(
            findings.GroupBy(FindingKey, StringComparer.Ordinal).Select(group => group.First()).Take(MovieProductionContinuityLimits.MaxFindings).ToArray(),
            recommendations.GroupBy(RecommendationKey, StringComparer.Ordinal).Select(group => group.First()).Take(MovieProductionContinuityLimits.MaxRecommendations).ToArray());
    }

    private static void ComparePair(
        MovieProductionContinuitySceneContext scene,
        MovieProductionContinuityShotContext previous,
        MovieProductionContinuityShotContext current,
        MovieProductionContinuityContext context,
        ICollection<DirectorStoryFindingDto> findings,
        ICollection<MovieScreenDirectionRecommendationDto> recommendations)
    {
        var prior = previous.ScreenDirection;
        var next = current.ScreenDirection;
        if (prior is null && next is null) return;
        var evidence = new[]
        {
            Evidence($"Shot {previous.Sequence} screen direction", previous.Id, prior),
            Evidence($"Shot {current.Sequence} screen direction", current.Id, next),
        };
        var target = Target(current.Id, $"Scene {scene.Sequence} · Shot {current.Sequence}");
        var reset = IsAxisReset(prior) || IsAxisReset(next);

        if (!reset && HasValues(prior?.Axis, next?.Axis) && !Same(prior!.Axis, next!.Axis))
        {
            Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.ScreenDirectionAxis,
                evidence, target, $"Adjacent shots use different screen-direction axes ('{prior.Axis}' then '{next.Axis}') without an explicit axis break.",
                "Edit the next shot's axis/orientation to preserve the established geography, or mark an intentional axis break and provide a bridging/geography beat; no shot was rewritten.", .92m,
                "A reverse angle or deliberate editorial reset may be intentional."));
            AddEdit(recommendations, current, previous, "axis mismatch", "Review the next shot's axis/orientation or explicitly document the intentional axis break.");
        }
        if (!reset && HasValues(prior?.Orientation, next?.Orientation) && !Same(prior!.Orientation, next!.Orientation) && !IsNeutral(prior!.Orientation) && !IsNeutral(next!.Orientation))
        {
            Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.ScreenDirectionAxis,
                evidence, target, $"Adjacent shots change camera orientation from '{prior.Orientation}' to '{next.Orientation}' without an explicit axis break.",
                "Confirm the cut is an intentional reverse angle; otherwise edit the next shot's orientation or add a safe geography bridge before production.", .86m,
                "Orientation labels are planning intent and do not prove the rendered camera position."));
            AddEdit(recommendations, current, previous, "orientation change", "Confirm or edit the next shot's camera orientation; do not silently flip the shot.");
        }
        if (!reset && prior?.ExitDirection is not null && next?.EntranceDirection is not null)
        {
            var expectedEntrance = OppositeSide(prior.ExitDirection);
            if (expectedEntrance is not null && !Same(expectedEntrance, next.EntranceDirection))
            {
                Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.ScreenDirectionEntranceExit,
                    evidence, target, $"Shot {previous.Sequence} exits on '{prior.ExitDirection}', but shot {current.Sequence} enters on '{next.EntranceDirection}'; the same-axis continuation would normally enter on '{expectedEntrance}'.",
                    "Edit the entrance direction to match the established travel geography, or mark an intentional reset and review the cut; no take or clip was changed.", .9m,
                    "A subject may have turned around or the scene may intentionally cross the axis."));
                AddEdit(recommendations, current, previous, "entrance/exit direction mismatch", $"Review the next shot's entrance side; expected '{expectedEntrance}' for same-axis continuation.");
            }
        }
        if (!reset && SameTarget(prior?.EyelineTarget, next?.EyelineTarget) && prior?.EyelineDirection is not null && next?.EyelineDirection is not null)
        {
            var expectedEyeline = OppositeSide(prior.EyelineDirection);
            if (expectedEyeline is not null && !Same(expectedEyeline, next.EyelineDirection))
            {
                Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.ScreenDirectionEyeline,
                    evidence, target, $"The adjacent eyelines to '{next.EyelineTarget}' point '{prior.EyelineDirection}' then '{next.EyelineDirection}' instead of the expected shot/reverse-shot change to '{expectedEyeline}'.",
                    "Edit the next eyeline direction or confirm that the subject is looking elsewhere; no performance, take, or camera data was changed.", .88m,
                    "Eyeline intent can change with blocking and does not prove the rendered gaze."));
                AddEdit(recommendations, current, previous, "eyeline mismatch", "Review the next eyeline direction and target before approving the cut.");
            }
        }
        if (!reset && HasValues(prior?.SpatialAnchor, next?.SpatialAnchor) && !Same(prior!.SpatialAnchor, next!.SpatialAnchor))
        {
            Add(findings, Finding(DirectorStoryFindingTypes.PossibleInconsistency, DirectorStoryFindingSeverities.Warning, DirectorStoryFindingCategories.ScreenDirectionGeography,
                evidence, target, $"Adjacent shots reference different spatial anchors ('{prior.SpatialAnchor}' then '{next.SpatialAnchor}') without a declared geography transition.",
                "Edit the next shot's spatial anchor or insert a neutral geography/establishing shot for review; automatic reordering or regeneration is not performed.", .84m,
                "The story may intentionally move to a new sub-location between cuts."));
            AddEdit(recommendations, current, previous, "spatial-anchor mismatch", "Review the next shot's anchor against the World plan and prior shot.");
            recommendations.Add(new MovieScreenDirectionRecommendationDto("insert", current.Id, previous.Id, "Spatial anchor changed between adjacent shots.", "Consider an explicitly approved neutral geography/bridging shot before the cut.", false));
        }
        if (prior is not null && next is null && (prior.ExitDirection is not null || prior.SpatialAnchor is not null))
        {
            recommendations.Add(new MovieScreenDirectionRecommendationDto("edit", current.Id, previous.Id, "The next shot has no screen-direction plan to compare against the outgoing geography.", "Add the missing axis, entrance, eyeline, or spatial-anchor intent before selecting a take.", false));
        }
    }

    private static void AddEdit(ICollection<MovieScreenDirectionRecommendationDto> recommendations, MovieProductionContinuityShotContext target, MovieProductionContinuityShotContext reference, string reason, string action) => recommendations.Add(new MovieScreenDirectionRecommendationDto("edit", target.Id, reference.Id, reason, action, false));
    private static bool IsAxisReset(MovieScreenDirectionPlan? plan) => plan?.AxisBreak == true || IsNeutral(plan?.Orientation) || Same(plan?.Orientation, MovieScreenDirectionValues.AxisBreak);
    private static bool IsNeutral(string? value) => Same(value, MovieScreenDirectionValues.Neutral);
    private static bool HasValues(string? first, string? second) => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second);
    private static bool Same(string? first, string? second) => string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameTarget(string? first, string? second) => HasValues(first, second) && Same(first, second);
    private static string? OppositeSide(string? value) => Same(value, MovieScreenDirectionValues.ScreenLeft) ? MovieScreenDirectionValues.ScreenRight : Same(value, MovieScreenDirectionValues.ScreenRight) ? MovieScreenDirectionValues.ScreenLeft : null;
    private static string FindingKey(DirectorStoryFindingDto finding) => $"{finding.FindingType}|{finding.Category}|{finding.AffectedTarget.TargetId}|{finding.Explanation}";
    private static string RecommendationKey(MovieScreenDirectionRecommendationDto item) => $"{item.Kind}|{item.TargetShotId}|{item.ReferenceShotId}|{item.Reason}";
    private static void Add(ICollection<DirectorStoryFindingDto> findings, DirectorStoryFindingDto finding) => findings.Add(finding);
    private static DirectorStoryFindingDto Finding(string type, string severity, string category, IReadOnlyList<DirectorStoryEvidenceDto> evidence, DirectorStoryFindingTargetDto target, string explanation, string correction, decimal confidence, string? uncertainty) => new(type, severity, category, evidence, target, explanation, correction, Math.Clamp(confidence, 0m, 1m), uncertainty);
    private static DirectorStoryFindingTargetDto Target(Guid id, string label) => new("movie_shot", id, label);
    private static DirectorStoryEvidenceDto Evidence(string source, Guid shotId, MovieScreenDirectionPlan? plan) => new(source, "screen_direction_plan", shotId, plan?.SchemaVersion is int version ? $"schema:{version}" : null, PlanExcerpt(plan));
    private static string PlanExcerpt(MovieScreenDirectionPlan? plan) => plan is null ? "No explicit screen-direction plan recorded." : JsonSerializer.Serialize(plan, Options);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

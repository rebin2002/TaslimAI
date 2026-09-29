using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class CinematographyPlanningValues
{
    public static readonly IReadOnlySet<string> ShotSizes = Values("extreme_wide", "wide", "full", "medium_wide", "medium", "medium_close_up", "close_up", "extreme_close_up");
    public static readonly IReadOnlySet<string> Framings = Values("single", "two_shot", "group", "over_shoulder", "profile", "point_of_view", "insert", "symmetrical");
    public static readonly IReadOnlySet<string> CameraAngles = Values("eye_level", "high_angle", "low_angle", "birds_eye", "worms_eye", "dutch");
    public static readonly IReadOnlySet<string> CameraPositions = Values("front", "three_quarter", "side", "behind", "elevated", "ground_level");
    public static readonly IReadOnlySet<string> CameraMovements = Values("locked_off", "pan", "tilt", "push_in", "pull_out", "dolly", "track", "orbit", "crane", "handheld", "whip_pan");
    public static readonly IReadOnlySet<string> CompositionIntents = Values("establish", "isolate", "connect", "contrast", "reveal", "disorient", "follow_action", "balance", "negative_space", "symmetry");
    public static readonly IReadOnlySet<string> LensLookIntents = Values("neutral", "wide_expansive", "compressed_telephoto", "portrait_natural", "distorted", "soft_dreamlike", "macro_detail", "documentary");
    public static readonly IReadOnlySet<string> Depths = Values("flat_graphic", "shallow", "layered", "deep");
    public static readonly IReadOnlySet<string> FocusIntents = Values("subject_locked", "rack_focus", "deep_focus", "soft_focus", "selective", "pull_focus");
    public static readonly IReadOnlySet<string> LightingIntents = Values("naturalistic", "soft_motivated", "high_contrast", "low_key", "high_key", "silhouette", "backlit", "practical_driven", "color_contrast");
    public static readonly IReadOnlySet<string> SubjectEmphases = Values("primary_subject", "face", "eyes", "gesture", "relationship", "environment", "object", "movement");
    public static readonly IReadOnlySet<string> VisualTransitionIntents = Values("none", "cut", "match_cut", "dissolve", "whip_pan_transition", "graphic_match", "hold", "reveal_transition");

    private static IReadOnlySet<string> Values(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

public sealed record CinematographyGroundingReference(
    string Source,
    string Evidence,
    bool Locked = false);

/// <summary>
/// The canonical provider-neutral cinematography contract for one Movie shot.
/// Every production decision is a closed value; creative notes remain optional and bounded.
/// </summary>
public sealed record CinematographyShotPlan(
    string ShotSize,
    string Framing,
    string CameraAngle,
    string CameraPosition,
    string CameraMovement,
    string CompositionIntent,
    string LensLookIntent,
    string Depth,
    string FocusIntent,
    string LightingIntent,
    string SubjectEmphasis,
    string VisualTransitionIntent,
    string? CreativeNotes = null,
    IReadOnlyList<CinematographyGroundingReference>? Grounding = null,
    int? LockedGuideRevisionNumber = null,
    string? Intent = null,
    string? PresetId = null);

public sealed record CinematographyPlanningContext(
    string AspectRatio,
    string Story,
    string Scene,
    string Shot,
    string? CharacterEmotionalPurpose,
    string? GuideCinematographyBible,
    CinematographyShotPlan? LockedCanon,
    int? LockedGuideRevisionNumber,
    IReadOnlyList<string> ContinuityConstraints,
    string? PreviousShot);

public sealed record CinematographyPlanningResult(
    CinematographyShotPlan Plan,
    bool GuideGrounded,
    bool CanonPreserved,
    IReadOnlyList<string> AppliedCanonFields);

public sealed class MovieCinematographyPlanRequest
{
    public string? CharacterEmotionalPurpose { get; set; }
    public string? CreativeNotes { get; set; }
    public CinematographyShotPlan? Overrides { get; set; }
}

public sealed record MovieCinematographyPlanResponse(
    Guid MovieProjectId,
    Guid SceneId,
    Guid ShotId,
    string AspectRatio,
    bool GuideGrounded,
    bool CanonPreserved,
    int? LockedGuideRevisionNumber,
    IReadOnlyList<string> AppliedCanonFields,
    CinematographyShotPlan Plan);

public sealed record CinematographyPlanningValueCatalog(
    IReadOnlyList<string> ShotSizes,
    IReadOnlyList<string> Framings,
    IReadOnlyList<string> CameraAngles,
    IReadOnlyList<string> CameraPositions,
    IReadOnlyList<string> CameraMovements,
    IReadOnlyList<string> CompositionIntents,
    IReadOnlyList<string> LensLookIntents,
    IReadOnlyList<string> Depths,
    IReadOnlyList<string> FocusIntents,
    IReadOnlyList<string> LightingIntents,
    IReadOnlyList<string> SubjectEmphases,
    IReadOnlyList<string> VisualTransitionIntents)
{
    public static CinematographyPlanningValueCatalog Current => new(
        Sorted(CinematographyPlanningValues.ShotSizes), Sorted(CinematographyPlanningValues.Framings),
        Sorted(CinematographyPlanningValues.CameraAngles), Sorted(CinematographyPlanningValues.CameraPositions),
        Sorted(CinematographyPlanningValues.CameraMovements), Sorted(CinematographyPlanningValues.CompositionIntents),
        Sorted(CinematographyPlanningValues.LensLookIntents), Sorted(CinematographyPlanningValues.Depths),
        Sorted(CinematographyPlanningValues.FocusIntents), Sorted(CinematographyPlanningValues.LightingIntents),
        Sorted(CinematographyPlanningValues.SubjectEmphases), Sorted(CinematographyPlanningValues.VisualTransitionIntents));

    private static IReadOnlyList<string> Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
}

public static class CinematographyShotPlanValidator
{
    private const int MaxCreativeNotesLength = 4_000;
    private const int MaxGroundingReferences = 24;
    private const int MaxGroundingEvidenceLength = 800;

    public static string? Validate(CinematographyShotPlan? plan)
    {
        if (plan is null) return "A structured cinematography plan is required.";
        if (!CinematographyPlanningValues.ShotSizes.Contains(plan.ShotSize) ||
            !CinematographyPlanningValues.Framings.Contains(plan.Framing) ||
            !CinematographyPlanningValues.CameraAngles.Contains(plan.CameraAngle) ||
            !CinematographyPlanningValues.CameraPositions.Contains(plan.CameraPosition) ||
            !CinematographyPlanningValues.CameraMovements.Contains(plan.CameraMovement) ||
            !CinematographyPlanningValues.CompositionIntents.Contains(plan.CompositionIntent) ||
            !CinematographyPlanningValues.LensLookIntents.Contains(plan.LensLookIntent) ||
            !CinematographyPlanningValues.Depths.Contains(plan.Depth) ||
            !CinematographyPlanningValues.FocusIntents.Contains(plan.FocusIntent) ||
            !CinematographyPlanningValues.LightingIntents.Contains(plan.LightingIntent) ||
            !CinematographyPlanningValues.SubjectEmphases.Contains(plan.SubjectEmphasis) ||
            !CinematographyPlanningValues.VisualTransitionIntents.Contains(plan.VisualTransitionIntent))
            return "Every cinematography plan field must use a supported structured value.";
        if (plan.CreativeNotes?.Trim().Length > MaxCreativeNotesLength)
            return "Cinematography creative notes must be 4,000 characters or fewer.";
        if (plan.Grounding is { Count: > MaxGroundingReferences })
            return "Cinematography grounding is limited to 24 references.";
        if (plan.Grounding is not null && plan.Grounding.Any(item =>
                string.IsNullOrWhiteSpace(item.Source) || item.Source.Trim().Length > 80 ||
                string.IsNullOrWhiteSpace(item.Evidence) || item.Evidence.Trim().Length > MaxGroundingEvidenceLength))
            return "Cinematography grounding references are invalid.";
        if (plan.Intent is not null && !CinematographyIntent.Supported.Contains(plan.Intent.Trim()))
            return "Cinematography intent is not supported.";
        return null;
    }

    public static CinematographyShotPlan Normalize(CinematographyShotPlan plan)
    {
        var normalized = plan with
        {
            ShotSize = NormalizeValue(plan.ShotSize),
            Framing = NormalizeValue(plan.Framing),
            CameraAngle = NormalizeValue(plan.CameraAngle),
            CameraPosition = NormalizeValue(plan.CameraPosition),
            CameraMovement = NormalizeValue(plan.CameraMovement),
            CompositionIntent = NormalizeValue(plan.CompositionIntent),
            LensLookIntent = NormalizeValue(plan.LensLookIntent),
            Depth = NormalizeValue(plan.Depth),
            FocusIntent = NormalizeValue(plan.FocusIntent),
            LightingIntent = NormalizeValue(plan.LightingIntent),
            SubjectEmphasis = NormalizeValue(plan.SubjectEmphasis),
            VisualTransitionIntent = NormalizeValue(plan.VisualTransitionIntent),
            CreativeNotes = Clean(plan.CreativeNotes, MaxCreativeNotesLength),
            Grounding = plan.Grounding?.Where(item => item is not null)
                .Select(item => new CinematographyGroundingReference(Clean(item.Source, 80)!, Clean(item.Evidence, MaxGroundingEvidenceLength)!, item.Locked))
                .ToArray(),
            Intent = string.IsNullOrWhiteSpace(plan.Intent) ? null : plan.Intent.Trim().ToLowerInvariant(),
            PresetId = Clean(plan.PresetId, 120),
        };
        return normalized;
    }

    public static string ToJson(CinematographyShotPlan plan) =>
        JsonSerializer.Serialize(Normalize(plan), new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    public static CinematographyShotPlan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<CinematographyShotPlan>(json);
            return plan is null || Validate(plan) is not null ? null : Normalize(plan);
        }
        catch (JsonException) { return null; }
    }

    private static string NormalizeValue(string value) => value.Trim().ToLowerInvariant();
    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}

public static class MovieCinematographyPlanner
{
    public static CinematographyPlanningResult Plan(CinematographyPlanningContext context, CinematographyShotPlan? requestedOverrides = null, string? creativeNotes = null)
    {
        if (string.IsNullOrWhiteSpace(context.AspectRatio) || !MovieStudioValidation.SupportedAspectRatios.Contains(context.AspectRatio.Trim()))
            throw new MovieStudioValidationException("A supported movie aspect ratio is required for cinematography planning.");
        if (string.IsNullOrWhiteSpace(context.Scene) || string.IsNullOrWhiteSpace(context.Shot))
            throw new MovieStudioValidationException("Scene and shot context are required for cinematography planning.");

        var text = Join(context.Story, context.Scene, context.Shot, context.CharacterEmotionalPurpose, context.GuideCinematographyBible, string.Join(" ", context.ContinuityConstraints));
        var emotional = Join(context.CharacterEmotionalPurpose, context.Shot);
        var vertical = context.AspectRatio.Trim() is "9:16" or "4:5";
        var candidate = new CinematographyShotPlan(
            Pick(text, ("extreme_close_up", "eyes intimate detail"), ("close_up", "face emotion confession"), ("wide", "establish landscape environment"), ("medium_wide", "enter crosses"), ("medium", "dialogue conversation")),
            Pick(text, ("point_of_view", "pov through her eyes through his eyes"), ("over_shoulder", "dialogue conversation"), ("two_shot", "together relationship"), ("single", "subject character")),
            Pick(emotional, ("low_angle", "threat power dominant"), ("high_angle", "vulnerable small exposed"), ("dutch", "disorient unstable"), ("eye_level", "intimate connection")),
            Pick(text, ("behind", "retreat leaves"), ("side", "profile lateral"), ("three_quarter", "dialogue relationship"), (vertical ? "front" : "three_quarter", "")),
            Pick(text, ("whip_pan", "whip sudden"), ("track", "runs chases crosses"), ("push_in", "realizes reveal emotion"), ("dolly", "approach enters"), ("handheld", "panic chaos"), ("locked_off", "still waits quiet")),
            Pick(text, ("disorient", "confusion panic unstable"), ("reveal", "reveals discovery enters"), ("connect", "together relationship intimate"), ("negative_space", "alone separation absence"), ("establish", "establish environment")),
            Pick(text, ("distorted", "uneasy dread"), ("compressed_telephoto", "surveillance distant"), ("wide_expansive", "landscape scale environment"), (vertical ? "portrait_natural" : "neutral", "")),
            Pick(text, ("shallow", "face eyes intimate emotion"), ("flat_graphic", "graphic silhouette"), ("deep", "geography environment landscape"), ("layered", "foreground background space")),
            Pick(text, ("rack_focus", "shifts notices reveal"), ("pull_focus", "attention turns"), ("deep_focus", "geography environment"), ("selective", "face eyes emotion"), ("subject_locked", "subject dialogue")),
            Pick(Join(context.GuideCinematographyBible, text), ("silhouette", "silhouette backlit"), ("low_key", "night dread shadow"), ("high_key", "bright joy"), ("color_contrast", "color neon"), ("backlit", "dawn sunset backlight"), ("soft_motivated", "window soft gentle"), ("naturalistic", "day available")),
            Pick(emotional, ("eyes", "eyes watch sees"), ("face", "face expression emotion"), ("gesture", "hand touch gesture"), ("relationship", "together between connection"), ("movement", "runs chases crosses"), ("environment", "place landscape room"), ("primary_subject", "subject character")),
            Pick(context.PreviousShot, ("match_cut", "match same movement same shape"), ("dissolve", "memory dream"), ("hold", "still wait"), ("reveal_transition", "reveal"), ("cut", "")));

        if (requestedOverrides is not null)
            candidate = ApplyOverrides(candidate, requestedOverrides);
        var applied = new List<string>();
        var canon = context.LockedCanon;
        if (canon is not null)
            candidate = ApplyCanon(candidate, canon, applied);

        var grounding = BuildGrounding(context, applied.Count > 0);
        var plan = CinematographyShotPlanValidator.Normalize(candidate with
        {
            CreativeNotes = Clean(creativeNotes ?? requestedOverrides?.CreativeNotes ?? candidate.CreativeNotes),
            Grounding = grounding,
            LockedGuideRevisionNumber = context.LockedGuideRevisionNumber,
            Intent = canon?.Intent ?? candidate.Intent,
            PresetId = canon?.PresetId ?? candidate.PresetId,
        });
        var validation = CinematographyShotPlanValidator.Validate(plan);
        if (validation is not null) throw new MovieStudioValidationException(validation);
        return new CinematographyPlanningResult(plan, !string.IsNullOrWhiteSpace(context.GuideCinematographyBible), canon is null || applied.Count >= 0, applied);
    }

    private static CinematographyShotPlan ApplyCanon(CinematographyShotPlan candidate, CinematographyShotPlan canon, ICollection<string> applied)
    {
        var result = candidate;
        result = Keep(result, canon.ShotSize, nameof(CinematographyShotPlan.ShotSize), applied, (item, value) => item with { ShotSize = value });
        result = Keep(result, canon.Framing, nameof(CinematographyShotPlan.Framing), applied, (item, value) => item with { Framing = value });
        result = Keep(result, canon.CameraAngle, nameof(CinematographyShotPlan.CameraAngle), applied, (item, value) => item with { CameraAngle = value });
        result = Keep(result, canon.CameraPosition, nameof(CinematographyShotPlan.CameraPosition), applied, (item, value) => item with { CameraPosition = value });
        result = Keep(result, canon.CameraMovement, nameof(CinematographyShotPlan.CameraMovement), applied, (item, value) => item with { CameraMovement = value });
        result = Keep(result, canon.CompositionIntent, nameof(CinematographyShotPlan.CompositionIntent), applied, (item, value) => item with { CompositionIntent = value });
        result = Keep(result, canon.LensLookIntent, nameof(CinematographyShotPlan.LensLookIntent), applied, (item, value) => item with { LensLookIntent = value });
        result = Keep(result, canon.Depth, nameof(CinematographyShotPlan.Depth), applied, (item, value) => item with { Depth = value });
        result = Keep(result, canon.FocusIntent, nameof(CinematographyShotPlan.FocusIntent), applied, (item, value) => item with { FocusIntent = value });
        result = Keep(result, canon.LightingIntent, nameof(CinematographyShotPlan.LightingIntent), applied, (item, value) => item with { LightingIntent = value });
        result = Keep(result, canon.SubjectEmphasis, nameof(CinematographyShotPlan.SubjectEmphasis), applied, (item, value) => item with { SubjectEmphasis = value });
        result = Keep(result, canon.VisualTransitionIntent, nameof(CinematographyShotPlan.VisualTransitionIntent), applied, (item, value) => item with { VisualTransitionIntent = value });
        return result with { CreativeNotes = canon.CreativeNotes ?? result.CreativeNotes };
    }

    private static CinematographyShotPlan Keep(CinematographyShotPlan candidate, string? value, string field, ICollection<string> applied, Func<CinematographyShotPlan, string, CinematographyShotPlan> replace)
    {
        if (string.IsNullOrWhiteSpace(value)) return candidate;
        applied.Add(field);
        return replace(candidate, value);
    }

    private static CinematographyShotPlan ApplyOverrides(CinematographyShotPlan candidate, CinematographyShotPlan overrides) => candidate with
    {
        ShotSize = string.IsNullOrWhiteSpace(overrides.ShotSize) ? candidate.ShotSize : overrides.ShotSize,
        Framing = string.IsNullOrWhiteSpace(overrides.Framing) ? candidate.Framing : overrides.Framing,
        CameraAngle = string.IsNullOrWhiteSpace(overrides.CameraAngle) ? candidate.CameraAngle : overrides.CameraAngle,
        CameraPosition = string.IsNullOrWhiteSpace(overrides.CameraPosition) ? candidate.CameraPosition : overrides.CameraPosition,
        CameraMovement = string.IsNullOrWhiteSpace(overrides.CameraMovement) ? candidate.CameraMovement : overrides.CameraMovement,
        CompositionIntent = string.IsNullOrWhiteSpace(overrides.CompositionIntent) ? candidate.CompositionIntent : overrides.CompositionIntent,
        LensLookIntent = string.IsNullOrWhiteSpace(overrides.LensLookIntent) ? candidate.LensLookIntent : overrides.LensLookIntent,
        Depth = string.IsNullOrWhiteSpace(overrides.Depth) ? candidate.Depth : overrides.Depth,
        FocusIntent = string.IsNullOrWhiteSpace(overrides.FocusIntent) ? candidate.FocusIntent : overrides.FocusIntent,
        LightingIntent = string.IsNullOrWhiteSpace(overrides.LightingIntent) ? candidate.LightingIntent : overrides.LightingIntent,
        SubjectEmphasis = string.IsNullOrWhiteSpace(overrides.SubjectEmphasis) ? candidate.SubjectEmphasis : overrides.SubjectEmphasis,
        VisualTransitionIntent = string.IsNullOrWhiteSpace(overrides.VisualTransitionIntent) ? candidate.VisualTransitionIntent : overrides.VisualTransitionIntent,
        CreativeNotes = overrides.CreativeNotes ?? candidate.CreativeNotes,
        Intent = overrides.Intent ?? candidate.Intent,
        PresetId = overrides.PresetId ?? candidate.PresetId,
    };

    private static IReadOnlyList<CinematographyGroundingReference> BuildGrounding(CinematographyPlanningContext context, bool canonApplied)
    {
        var result = new List<CinematographyGroundingReference>();
        Add(result, "story", context.Story, false);
        Add(result, "scene", context.Scene, false);
        Add(result, "shot", context.Shot, false);
        Add(result, "aspect_ratio", context.AspectRatio, false);
        Add(result, "character_emotional_purpose", context.CharacterEmotionalPurpose, false);
        Add(result, "cinematography_bible", context.GuideCinematographyBible, context.LockedCanon is not null);
        foreach (var constraint in context.ContinuityConstraints) Add(result, "continuity", constraint, true);
        Add(result, "previous_shot", context.PreviousShot, false);
        return result;
    }

    private static void Add(ICollection<CinematographyGroundingReference> result, string source, string? evidence, bool locked)
    {
        if (!string.IsNullOrWhiteSpace(evidence)) result.Add(new(source, Limit(evidence, 800), locked));
    }

    private static string Pick(string? text, params (string Value, string Terms)[] options)
    {
        var source = text?.ToLowerInvariant() ?? string.Empty;
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Terms)) continue;
            if (option.Terms.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(source.Contains)) return option.Value;
        }
        return options.Last().Value;
    }

    private static string Join(params string?[] values) => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 4_000)];
}

public interface IMovieCinematographyPlanningService
{
    Task<MovieCinematographyPlanResponse?> PlanAsync(Guid userId, Guid shotId, MovieCinematographyPlanRequest request, CancellationToken cancellationToken = default);
}

public sealed class MovieCinematographyPlanningService(TaslimDbContext db, MovieCollaborationAccess collaboration) : IMovieCinematographyPlanningService
{
    public async Task<MovieCinematographyPlanResponse?> PlanAsync(Guid userId, Guid shotId, MovieCinematographyPlanRequest request, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).ThenInclude(item => item.Revisions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (shot.Status == MovieShotStatuses.Archived) throw new MovieStudioValidationException("Archived shots cannot receive a new cinematography plan.");

        var movie = shot.Scene.MovieProject;
        var guide = movie.Guide;
        var lockedRevision = guide.LockedRevisionNumber is int revisionNumber
            ? guide.Revisions.FirstOrDefault(item => item.RevisionNumber == revisionNumber && item.Status == MovieGuideRevisionStatuses.Locked)
            : null;
        var guideSelection = CinematographyIntentValidator.Normalize(CinematographyIntentValidator.FromJson(guide.CinematographyBibleReferencesJson));
        var lockedCanon = ReadLockedCanon(guideSelection, lockedRevision?.CinematographyBibleJson);
        var story = await db.MovieStoryRevisions.AsNoTracking()
            .Include(item => item.MovieStory)
            .Include(item => item.Scenes).ThenInclude(item => item.Elements)
            .Where(item => item.MovieStory.MovieProjectId == movie.Id && item.MovieStory.ApprovedRevisionId == item.Id && item.Status == MovieStoryRevisionStatuses.Approved)
            .FirstOrDefaultAsync(cancellationToken);
        var storyContext = story is null ? movie.Description : string.Join(" ", story.Premise, story.Logline, story.Synopsis, story.Treatment, story.Scenes.Where(item => item.MovieSceneId == shot.MovieSceneId).Select(item => string.Join(" ", item.Slugline, item.Synopsis, item.Elements.OrderBy(element => element.Ordinal).Select(element => element.Content))).FirstOrDefault());
        var characterIds = MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson);
        var characterNotes = characterIds.Count == 0 ? null : string.Join(" ", await db.MovieCharacters.AsNoTracking().Where(item => characterIds.Contains(item.Id)).Select(item => string.Join(" ", item.Name, item.PersonalityAndStoryNotes, item.VoiceAndPerformance, item.ContinuityNotes)).ToArrayAsync(cancellationToken));
        var previousShot = await db.MovieShots.AsNoTracking().Where(item => item.MovieSceneId == shot.MovieSceneId && item.Sequence < shot.Sequence).OrderByDescending(item => item.Sequence).Select(item => string.Join(" ", item.Description, item.Purpose, item.CinematographyJson)).FirstOrDefaultAsync(cancellationToken);
        var continuity = await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movie.Id && (item.ScopeType == MovieWorldScopes.Project || item.ScopeType == MovieWorldScopes.Scene && item.ScopeId == shot.MovieSceneId || item.ScopeType == MovieWorldScopes.Shot && item.ScopeId == shot.Id)).Select(item => string.Join(" ", item.FactKey, item.FactValue, item.Notes)).ToArrayAsync(cancellationToken);
        var context = new CinematographyPlanningContext(
            movie.AspectRatio,
            storyContext,
            string.Join(" ", shot.Scene.Title, shot.Scene.Summary, shot.Scene.ContinuityNotes),
            string.Join(" ", shot.Description, shot.Purpose, shot.Subjects, shot.Dialogue, shot.VisualContinuityNotes),
            string.IsNullOrWhiteSpace(request.CharacterEmotionalPurpose) ? characterNotes : request.CharacterEmotionalPurpose,
            string.Join(" ", guide.CameraLanguage, guide.ColorAndLighting, guideSelection?.Intent, guideSelection?.PresetId, lockedRevision?.CinematographyBibleJson),
            lockedCanon,
            guide.LockedRevisionNumber,
            continuity.Concat(new[] { movie.Guide.ContinuityRules, shot.ContinuityReferences }).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
            previousShot);
        var result = MovieCinematographyPlanner.Plan(context, request.Overrides, request.CreativeNotes);
        shot.CinematographyJson = CinematographyShotPlanValidator.ToJson(result.Plan);
        shot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieCinematographyPlanResponse(movie.Id, shot.MovieSceneId, shot.Id, movie.AspectRatio, result.GuideGrounded, result.CanonPreserved, guide.LockedRevisionNumber, result.AppliedCanonFields, result.Plan);
    }

    private static CinematographyShotPlan? ReadLockedCanon(CinematographyIntentSelection? legacy, string? lockedBibleJson)
    {
        var direct = CinematographyShotPlanValidator.FromJson(lockedBibleJson);
        if (direct is not null) return direct;
        if (legacy is null) return null;
        var key = legacy.Intent?.Trim().ToLowerInvariant() ?? string.Empty;
        var canon = key switch
        {
            CinematographyIntent.Intimate => Canon("close_up", "single", "eye_level", "three_quarter", "push_in", "isolate", "portrait_natural", "shallow", "subject_locked", "soft_motivated", "face", "cut"),
            CinematographyIntent.Natural => Canon("medium", "single", "eye_level", "three_quarter", "locked_off", "establish", "documentary", "layered", "subject_locked", "naturalistic", "primary_subject", "cut"),
            CinematographyIntent.Epic => Canon("wide", "single", "low_angle", "three_quarter", "crane", "establish", "wide_expansive", "deep", "deep_focus", "backlit", "environment", "reveal_transition"),
            CinematographyIntent.Dynamic => Canon("medium", "single", "low_angle", "three_quarter", "track", "follow_action", "neutral", "layered", "pull_focus", "high_contrast", "movement", "cut"),
            _ => null,
        };
        return canon is null ? null : canon with { Intent = legacy.Intent, PresetId = legacy.PresetId, CreativeNotes = legacy.Notes };

        static CinematographyShotPlan Canon(string shotSize, string framing, string angle, string position, string movement, string composition, string lens, string depth, string focus, string lighting, string subject, string transition) =>
            new(shotSize, framing, angle, position, movement, composition, lens, depth, focus, lighting, subject, transition);
    }
}

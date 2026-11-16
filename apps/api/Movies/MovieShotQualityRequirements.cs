using System.Text.Json;

namespace Taslim.Api.Movies;

public static class MovieShotQualityRequirementKeys
{
    public const string FacialFidelity = "facial_fidelity";
    public const string HandBodyFidelity = "hand_body_fidelity";
    public const string TemporalConsistency = "temporal_consistency";
    public const string CharacterConsistency = "character_consistency";
    public const string EnvironmentConsistency = "environment_consistency";
    public const string FineDetailPreservation = "fine_detail_preservation";
    public const string MotionFidelity = "motion_fidelity";
    public const string TextSignageFidelity = "text_signage_fidelity";
    public const string LipSyncRequirement = "lip_sync_requirement";
    public const string VfxFidelity = "vfx_fidelity";
    public const string UpscaleSuitability = "upscale_suitability";
    public const string ContinuityFidelity = "continuity_fidelity";

    public static readonly IReadOnlyList<string> All =
    [
        FacialFidelity, HandBodyFidelity, TemporalConsistency, CharacterConsistency,
        EnvironmentConsistency, FineDetailPreservation, MotionFidelity, TextSignageFidelity,
        LipSyncRequirement, VfxFidelity, UpscaleSuitability, ContinuityFidelity,
    ];
}

public static class MovieShotQualityLevels
{
    public const string None = "none";
    public const string Low = "low";
    public const string Moderate = "moderate";
    public const string High = "high";
    public const string Critical = "critical";

    public static string FromScore(int score) => Math.Clamp(score, 0, 4) switch
    {
        0 => None,
        1 => Low,
        2 => Moderate,
        3 => High,
        _ => Critical,
    };
}

public sealed record MovieShotQualityRequirementDto(
    string Key,
    int Score,
    string Level,
    bool Required,
    string Rationale);

public sealed record MovieShotQualityRequirementsDto(
    string ProfileVersion,
    IReadOnlyList<MovieShotQualityRequirementDto> Requirements,
    IReadOnlyList<string> GroundingSignals)
{
    public int ScoreFor(string key) => Requirements.FirstOrDefault(item =>
        string.Equals(item.Key, key, StringComparison.Ordinal))?.Score ?? 0;
}

/// <summary>
/// Provider-neutral input for a future Adaptive Resolution Director. It describes
/// preservation requirements only; it intentionally contains no provider, model,
/// output-resolution, credit, or charging decision.
/// </summary>
public sealed record MovieAdaptiveResolutionDirectorInputDto(
    string ContractVersion,
    Guid MovieProjectId,
    Guid MovieSceneId,
    Guid MovieShotId,
    string ShotDescription,
    string? ProjectStyle,
    MovieShotQualityRequirementsDto QualityRequirements,
    IReadOnlyList<string> PreservationPriorities,
    IReadOnlyList<string> ContinuityAnchors);

public sealed record MovieShotQualityProfileDto(
    MovieShotQualityRequirementsDto QualityRequirements,
    MovieAdaptiveResolutionDirectorInputDto AdaptiveResolutionDirectorInput);

public sealed record MovieShotQualityPlanningContext(
    Guid MovieProjectId,
    Guid MovieSceneId,
    string ProjectTitle,
    string ProjectDescription,
    string ProjectStyle,
    string? ProjectVisualLanguage,
    string? ProjectCameraLanguage,
    string? ProjectColorAndLighting,
    string? ProjectContinuityRules,
    string? SceneSummary,
    IReadOnlyList<string> CharacterContinuityAnchors,
    IReadOnlyList<string> WorldContinuityAnchors);

public static class MovieShotQualityRequirementsPlanner
{
    public const string ProfileVersion = "movie-shot-quality-v1";
    public const string AdaptiveResolutionContractVersion = "adaptive-resolution-input-v1";
    private const int MaxGroundingSignals = 24;
    private const int MaxContinuityAnchors = 24;
    private const int MaxRationaleLength = 240;

    public static MovieShotQualityProfileDto Plan(MovieShot shot, MovieShotQualityPlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(context);
        if (shot.MovieSceneId != context.MovieSceneId)
            throw new ArgumentException("Quality context scene does not match the shot.", nameof(context));
        if (shot.Scene?.MovieProjectId is Guid projectId && projectId != Guid.Empty && context.MovieProjectId != Guid.Empty && projectId != context.MovieProjectId)
            throw new ArgumentException("Quality context project does not match the shot.", nameof(context));

        var text = Normalize(string.Join(" ",
        [
            shot.Description, shot.Purpose, shot.Subjects, shot.LocationSet,
            shot.ProductionRequirements, shot.ContinuityReferences, shot.CameraAndFraming,
            shot.CameraMotion, shot.CinematographyJson, shot.Narration, shot.Dialogue,
            shot.VisualContinuityNotes, context.ProjectTitle, context.ProjectDescription,
            context.ProjectStyle, context.ProjectVisualLanguage, context.ProjectCameraLanguage,
            context.ProjectColorAndLighting, context.ProjectContinuityRules, context.SceneSummary,
            string.Join(" ", context.CharacterContinuityAnchors),
            string.Join(" ", context.WorldContinuityAnchors),
        ]));
        var hasCloseUp = ContainsAny(text, "close-up", "close up", "closeup", "extreme close", "portrait", "headshot", "medium close");
        var hasFace = ContainsAny(text, "face", "facial", "eye", "eyes", "expression", "tears", "smile", "portrait");
        var hasCharacter = MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson).Count > 0
            || ContainsAny(text, "character", "person", "people", "man", "woman", "child", "lead", "protagonist", "hero", "villain")
            || context.CharacterContinuityAnchors.Count > 0;
        var hasHandsOrBody = ContainsAny(text, "hand", "hands", "finger", "fingers", "body", "full-body", "full body", "gesture", "holding", "carrying", "walk", "running", "dance");
        var hasEnvironment = !string.IsNullOrWhiteSpace(shot.LocationSet)
            || context.WorldContinuityAnchors.Count > 0
            || ContainsAny(text, "landscape", "environment", "establishing", "exterior", "interior", "street", "forest", "mountain", "sky", "sea", "city", "warehouse");
        var hasMotion = !string.IsNullOrWhiteSpace(shot.CameraMotion)
            || ContainsAny(text, "motion", "moving", "move", "push", "pull", "pan", "tilt", "dolly", "tracking", "zoom", "running", "chase", "fight", "explosion");
        var hasText = ContainsAny(text, "text", "sign", "signage", "label", "logo", "letter", "letters", "subtitle", "screen", "readable", "written");
        var hasDialogue = !string.IsNullOrWhiteSpace(shot.Dialogue)
            || ContainsAny(text, "speaking", "speak", "talking", "talk", "dialogue", "lip", "lips", "says");
        var hasVfx = ContainsAny(text, "vfx", "visual effect", "visual effects", "explosion", "fire", "smoke", "particle", "particles", "magic", "hologram", "lightning", "creature", "composite");
        var hasDetail = hasCloseUp || hasText || ContainsAny(text, "detail", "texture", "fine", "intricate", "hero prop", "readable", "crack", "jewelry", "fabric");
        var hasContinuity = !string.IsNullOrWhiteSpace(shot.ContinuityReferences)
            || !string.IsNullOrWhiteSpace(shot.VisualContinuityNotes)
            || !string.IsNullOrWhiteSpace(context.ProjectContinuityRules)
            || context.CharacterContinuityAnchors.Count > 0
            || context.WorldContinuityAnchors.Count > 0;
        var duration = shot.DurationSeconds ?? 0;

        var requirements = new[]
        {
            Requirement(MovieShotQualityRequirementKeys.FacialFidelity,
                hasCloseUp ? 4 : hasFace ? 3 : hasDialogue && hasCharacter ? 2 : hasCharacter ? 1 : 0,
                hasCloseUp ? "Close framing makes facial identity and expression a primary preservation target." : hasFace ? "The shot content references facial features or expression." : hasCharacter && hasDialogue ? "A character speaks, but the shot does not establish a close facial view." : "No facial subject is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.HandBodyFidelity,
                hasHandsOrBody ? 4 : hasCharacter && hasMotion ? 3 : hasCharacter ? 2 : 0,
                hasHandsOrBody ? "Hands, body pose, or physical action is explicit in the shot content." : hasCharacter && hasMotion ? "A character performs motion without an explicit hand or body detail." : hasCharacter ? "A character is present but detailed body fidelity is not foregrounded." : "No character body is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.TemporalConsistency,
                hasMotion || duration >= 5 ? 4 : duration >= 2 ? 3 : 2,
                hasMotion ? "Camera or subject motion requires stable temporal behavior." : duration >= 5 ? "The planned duration requires stable temporal behavior across the shot." : "Every shot receives a bounded baseline for temporal stability."),
            Requirement(MovieShotQualityRequirementKeys.CharacterConsistency,
                hasCharacter && hasContinuity ? 4 : hasCharacter ? 3 : 0,
                hasCharacter && hasContinuity ? "Named or referenced characters are paired with continuity context." : hasCharacter ? "A character is grounded in the shot, without a stronger continuity anchor." : "No character identity is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.EnvironmentConsistency,
                hasEnvironment && hasContinuity ? 4 : hasEnvironment ? 3 : 1,
                hasEnvironment && hasContinuity ? "The shot has an environment and project or world continuity context." : hasEnvironment ? "The location or environment is explicit in the shot content." : "Environment is not foregrounded; only a bounded baseline is required."),
            Requirement(MovieShotQualityRequirementKeys.FineDetailPreservation,
                hasDetail ? 4 : hasCharacter || hasEnvironment ? 2 : 1,
                hasDetail ? "Close framing, text, texture, or a named hero detail is explicit." : hasCharacter || hasEnvironment ? "The shot contains ordinary visible content without a foregrounded fine-detail anchor." : "The shot has no grounded fine-detail target."),
            Requirement(MovieShotQualityRequirementKeys.MotionFidelity,
                hasMotion ? 4 : hasCharacter && ContainsAny(text, "action", "cross", "enter", "exit", "turn", "reach") ? 3 : 1,
                hasMotion ? "Camera or subject motion is explicit and must remain faithful." : hasCharacter ? "Character action is present but no specific motion instruction is dominant." : "The shot is not grounded as a motion-critical beat."),
            Requirement(MovieShotQualityRequirementKeys.TextSignageFidelity,
                hasText ? 4 : 0,
                hasText ? "Readable text, signage, labels, logos, or written content is explicit." : "No text or signage target is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.LipSyncRequirement,
                hasDialogue ? 4 : 0,
                hasDialogue ? "Dialogue or speaking/lip content makes synchronization a shot requirement." : "No dialogue or speaking target is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.VfxFidelity,
                hasVfx ? 4 : 0,
                hasVfx ? "A visual-effects or compositing element is explicit in the shot content." : "No visual-effects target is grounded in this shot."),
            Requirement(MovieShotQualityRequirementKeys.UpscaleSuitability,
                hasText || hasDetail || hasCloseUp ? 4 : hasCharacter || hasEnvironment ? 2 : 1,
                hasText || hasDetail || hasCloseUp ? "The shot contains detail that must survive a later quality-preserving upscale step." : hasCharacter || hasEnvironment ? "The shot contains visible subject or environment structure for a bounded upscale assessment." : "Only a bounded baseline is needed because no upscale-sensitive detail is grounded."),
            Requirement(MovieShotQualityRequirementKeys.ContinuityFidelity,
                hasContinuity ? 4 : hasCharacter || hasEnvironment ? 2 : 1,
                hasContinuity ? "Shot, character, world, or project continuity references are explicit." : hasCharacter || hasEnvironment ? "Visible subject or environment continuity is relevant but not explicitly locked." : "No continuity anchor is grounded beyond the shot itself."),
        };
        var grounding = BuildGroundingSignals(shot, context, hasCloseUp, hasCharacter, hasEnvironment, hasMotion, hasText, hasDialogue, hasVfx);
        var quality = new MovieShotQualityRequirementsDto(ProfileVersion, requirements, grounding);
        var priorities = requirements.Where(item => item.Required)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key).ToArray();
        var continuityAnchors = context.CharacterContinuityAnchors
            .Concat(context.WorldContinuityAnchors)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Bound(item.Trim(), 160))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxContinuityAnchors)
            .ToArray();
        var adaptiveInput = new MovieAdaptiveResolutionDirectorInputDto(
            AdaptiveResolutionContractVersion,
            context.MovieProjectId,
            shot.MovieSceneId,
            shot.Id,
            Bound(shot.Description.Trim(), 8_000),
            Bound(context.ProjectStyle, 100),
            quality,
            priorities,
            continuityAnchors);
        return new MovieShotQualityProfileDto(quality, adaptiveInput);
    }

    public static bool TryValidate(MovieShotQualityProfileDto? profile, out string? error)
    {
        error = null;
        if (profile is null)
        {
            error = "The shot quality profile is required.";
            return false;
        }
        if (profile.QualityRequirements is null || profile.AdaptiveResolutionDirectorInput is null)
        {
            error = "The shot quality profile is incomplete.";
            return false;
        }
        var requirements = profile.QualityRequirements;
        if (requirements.Requirements is null || requirements.GroundingSignals is null
            || profile.AdaptiveResolutionDirectorInput.PreservationPriorities is null
            || profile.AdaptiveResolutionDirectorInput.ContinuityAnchors is null
            || profile.AdaptiveResolutionDirectorInput.QualityRequirements is null)
        {
            error = "The shot quality profile contains incomplete bounded collections.";
            return false;
        }
        if (!string.Equals(requirements.ProfileVersion, ProfileVersion, StringComparison.Ordinal))
        {
            error = "The shot quality profile version is unsupported.";
            return false;
        }
        if (requirements.Requirements.Count != MovieShotQualityRequirementKeys.All.Count)
        {
            error = "The shot quality profile must contain exactly the supported requirement keys.";
            return false;
        }
        if (requirements.Requirements.Any(item => item is null))
        {
            error = "The shot quality profile contains a null requirement.";
            return false;
        }
        var keys = requirements.Requirements.Select(item => item.Key).ToArray();
        if (!keys.ToHashSet(StringComparer.Ordinal).SetEquals(MovieShotQualityRequirementKeys.All))
        {
            error = "The shot quality profile contains an unsupported or duplicate requirement key.";
            return false;
        }
        foreach (var requirement in requirements.Requirements)
        {
            if (requirement is null)
            {
                error = "The shot quality profile contains a null requirement.";
                return false;
            }
            if (requirement.Score is < 0 or > 4)
            {
                error = $"The {requirement.Key} score must be between 0 and 4.";
                return false;
            }
            if (!string.Equals(requirement.Level, MovieShotQualityLevels.FromScore(requirement.Score), StringComparison.Ordinal))
            {
                error = $"The {requirement.Key} level does not match its score.";
                return false;
            }
            if (requirement.Required != requirement.Score >= 2)
            {
                error = $"The {requirement.Key} required flag does not match its bounded score.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(requirement.Rationale) || requirement.Rationale.Length > MaxRationaleLength)
            {
                error = $"The {requirement.Key} rationale is missing or too long.";
                return false;
            }
        }
        var expectedPriorities = requirements.Requirements.Where(item => item.Required)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key);
        if (!profile.AdaptiveResolutionDirectorInput.PreservationPriorities.SequenceEqual(expectedPriorities, StringComparer.Ordinal))
        {
            error = "The Adaptive Resolution Director preservation priorities are not aligned with the bounded scores.";
            return false;
        }
        if (requirements.GroundingSignals.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 180)
            || profile.AdaptiveResolutionDirectorInput.ContinuityAnchors.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 160)
            || profile.AdaptiveResolutionDirectorInput.ShotDescription.Length > 8_000
            || profile.AdaptiveResolutionDirectorInput.ProjectStyle is { Length: > 100 })
        {
            error = "The shot quality grounding values exceed their bounded limits.";
            return false;
        }
        if (!string.Equals(profile.AdaptiveResolutionDirectorInput.ContractVersion, AdaptiveResolutionContractVersion, StringComparison.Ordinal)
            || !EquivalentRequirements(profile.AdaptiveResolutionDirectorInput.QualityRequirements, requirements))
        {
            error = "The Adaptive Resolution Director input is not aligned with the quality profile.";
            return false;
        }
        if (requirements.GroundingSignals.Count > MaxGroundingSignals || profile.AdaptiveResolutionDirectorInput.ContinuityAnchors.Count > MaxContinuityAnchors)
        {
            error = "The shot quality grounding context exceeds its bounded limits.";
            return false;
        }
        return true;
    }

    public static MovieShotQualityProfileDto PlanWithAvailableContext(MovieShot shot)
    {
        var project = shot.Scene?.MovieProject;
        return Plan(shot, new MovieShotQualityPlanningContext(
            project?.Id ?? Guid.Empty,
            shot.MovieSceneId,
            project?.Title ?? string.Empty,
            project?.Description ?? string.Empty,
            project?.Style ?? string.Empty,
            null,
            null,
            null,
            null,
            shot.Scene?.Summary,
            [],
            []));
    }

    public static string Serialize(MovieShotQualityProfileDto profile)
    {
        if (!TryValidate(profile, out var error)) throw new ArgumentException(error, nameof(profile));
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    public static bool TryParse(string? json, out MovieShotQualityProfileDto? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            profile = JsonSerializer.Deserialize<MovieShotQualityProfileDto>(json, JsonOptions);
            return TryValidate(profile, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static MovieShotQualityRequirementDto Requirement(string key, int score, string rationale) =>
        new(key, score, MovieShotQualityLevels.FromScore(score), score >= 2, Bound(rationale, MaxRationaleLength));

    private static bool EquivalentRequirements(MovieShotQualityRequirementsDto first, MovieShotQualityRequirementsDto second) =>
        string.Equals(first.ProfileVersion, second.ProfileVersion, StringComparison.Ordinal)
        && first.GroundingSignals.SequenceEqual(second.GroundingSignals, StringComparer.Ordinal)
        && first.Requirements.Count == second.Requirements.Count
        && first.Requirements.Zip(second.Requirements).All(pair => pair.First == pair.Second);

    private static IReadOnlyList<string> BuildGroundingSignals(
        MovieShot shot,
        MovieShotQualityPlanningContext context,
        bool hasCloseUp,
        bool hasCharacter,
        bool hasEnvironment,
        bool hasMotion,
        bool hasText,
        bool hasDialogue,
        bool hasVfx)
    {
        var signals = new List<string>();
        AddSignal(signals, "shot_description", shot.Description);
        AddSignal(signals, "shot_purpose", shot.Purpose);
        AddSignal(signals, "shot_subjects", shot.Subjects);
        AddSignal(signals, "shot_location", shot.LocationSet);
        AddSignal(signals, "shot_production_requirements", shot.ProductionRequirements);
        AddSignal(signals, "shot_continuity", shot.ContinuityReferences ?? shot.VisualContinuityNotes);
        AddSignal(signals, "scene_summary", context.SceneSummary);
        AddSignal(signals, "project_style", context.ProjectStyle);
        if (hasCloseUp) signals.Add("content:close_framing");
        if (hasCharacter) signals.Add("content:character_subject");
        if (hasEnvironment) signals.Add("content:environment_subject");
        if (hasMotion) signals.Add("content:motion");
        if (hasText) signals.Add("content:text_or_signage");
        if (hasDialogue) signals.Add("content:dialogue_or_speaking");
        if (hasVfx) signals.Add("content:vfx");
        signals.AddRange(context.CharacterContinuityAnchors.Take(4).Select(item => $"character_context:{Bound(item, 120)}"));
        signals.AddRange(context.WorldContinuityAnchors.Take(4).Select(item => $"world_context:{Bound(item, 120)}"));
        return signals.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => Bound(item, 180)).Distinct(StringComparer.Ordinal).Take(MaxGroundingSignals).ToArray();
    }

    private static void AddSignal(ICollection<string> signals, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) signals.Add($"{key}:{Bound(value.Trim(), 140)}");
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string Bound(string? value, int max) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= max ? value : value[..max].TrimEnd();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

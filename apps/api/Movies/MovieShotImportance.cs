using System.Globalization;

namespace Taslim.Api.Movies;

public static class MovieShotImportanceLevels
{
    public const string UtilityBackground = "utility/background";
    public const string Standard = "standard";
    public const string Important = "important";
    public const string Hero = "hero";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        UtilityBackground, Standard, Important, Hero,
    };

    public static string? Normalize(string? value) => Supported.FirstOrDefault(item =>
        string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record MovieShotImportanceSceneContext(
    string Title,
    string Summary,
    string? ContinuityNotes,
    string? Narration,
    string? Dialogue,
    string? ScreenplaySlugline = null,
    string? ScreenplaySynopsis = null,
    string? ScreenplayElements = null);

public sealed record MovieShotImportanceStoryContext(
    string Premise,
    string Logline,
    string Synopsis,
    string Treatment,
    string? ScreenplaySlugline,
    string? ScreenplaySynopsis,
    string? ScreenplayElements);

public sealed record MovieShotImportanceGuideContext(
    string VisualLanguage,
    string CameraLanguage,
    string ColorAndLighting,
    string SoundAndNarration,
    string ContinuityRules,
    string? StoryBibleJson,
    string? VisualBibleJson,
    string? CinematographyBibleJson,
    string? AudioBibleJson,
    string? ContinuityBibleJson,
    int? RevisionNumber,
    bool IsLocked);

public sealed record MovieShotImportanceContext(
    string? Description,
    string? Purpose,
    string? Subjects,
    string? ProductionRequirements,
    string? ContinuityReferences,
    string? CameraAndFraming,
    string? CameraMotion,
    string? CinematographyJson,
    string? Narration,
    string? Dialogue,
    string? VisualContinuityNotes,
    MovieShotImportanceSceneContext Scene,
    MovieShotImportanceStoryContext? Story,
    MovieShotImportanceGuideContext? Guide);

public sealed record MovieShotImportanceEvidenceDto(string Source, string Signal, string Excerpt);

public sealed record MovieShotImportanceAssessment(
    string Classification,
    string Reasoning,
    decimal Confidence,
    IReadOnlyList<MovieShotImportanceEvidenceDto> Evidence);

/// <summary>
/// Classifies narrative and production importance without a provider call. Camera/style fields
/// can support an important assessment, but never create a hero assessment on their own.
/// </summary>
public sealed class MovieShotImportanceClassifier
{
    private static readonly string[] HeroSignals =
    [
        "climax", "final showdown", "major reveal", "truth is revealed", "reveals the truth",
        "betrayal", "sacrifice", "dies", "death", "decisive action", "turning point", "irreversible",
        "final confrontation", "point of no return", "victory", "defeat",
    ];

    private static readonly string[] ImportantSignals =
    [
        "important introduction", "introduces", "introduction", "first appearance", "arrival", "enters",
        "confrontation", "escape", "discovers", "discovery", "evidence", "key moment", "key action",
        "critical establishing", "important establishing", "emotional beat", "emotional moment",
        "turning point", "decision", "decides", "close-up", "closeup",
    ];

    private static readonly string[] UtilitySignals =
    [
        "background", "ambient", "atmosphere", "texture", "insert", "filler", "transition", "b-roll",
        "plate shot", "empty frame", "cutaway", "establishing shot only",
    ];

    private static readonly string[] EmotionalSignals =
    ["emotional", "grief", "tears", "crying", "fear", "shock", "mourning", "relief", "despair", "love"];

    public MovieShotImportanceAssessment Classify(MovieShotImportanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evidence = new EvidenceCollector();
        var shotNarrative = Join(context.Description, context.Purpose, context.Subjects, context.ProductionRequirements,
            context.ContinuityReferences, context.Narration, context.Dialogue, context.VisualContinuityNotes);
        var sceneNarrative = Join(context.Scene.Title, context.Scene.Summary, context.Scene.ContinuityNotes,
            context.Scene.Narration, context.Scene.Dialogue, context.Scene.ScreenplaySlugline,
            context.Scene.ScreenplaySynopsis, context.Scene.ScreenplayElements);
        var storyNarrative = context.Story is null
            ? string.Empty
            : Join(context.Story.Premise, context.Story.Logline, context.Story.Synopsis, context.Story.Treatment,
                context.Story.ScreenplaySlugline, context.Story.ScreenplaySynopsis, context.Story.ScreenplayElements);
        var guideNarrative = context.Guide is null
            ? string.Empty
            : Join(context.Guide.StoryBibleJson, context.Guide.ContinuityRules, context.Guide.ContinuityBibleJson,
                context.Guide.SoundAndNarration, context.Guide.AudioBibleJson, context.Guide.VisualBibleJson,
                context.Guide.VisualLanguage, context.Guide.ColorAndLighting);

        AddSignals(evidence, "shot", shotNarrative, HeroSignals, "hero_narrative");
        AddSignals(evidence, "scene", sceneNarrative, HeroSignals, "hero_narrative");
        AddSignals(evidence, "story", storyNarrative, HeroSignals, "hero_narrative");
        AddSignals(evidence, "guide", guideNarrative, HeroSignals, "hero_narrative");

        var shotHasAction = HasAny(shotNarrative, "does", "runs", "takes", "holds", "opens", "breaks", "confronts",
            "escapes", "chooses", "decides", "reveals", "discovers", "enters", "leaves", "speaks", "says", "looks");
        var heroEvidence = evidence.Items.Count(item => item.Signal == "hero_narrative");
        if (heroEvidence > 0 && !string.IsNullOrWhiteSpace(context.Description) && shotHasAction)
        {
            var confidence = Math.Min(0.97m, 0.84m + (0.03m * Math.Min(heroEvidence, 3)));
            return new(
                MovieShotImportanceLevels.Hero,
                "Classified as hero because the bounded Story/Scene/Guide context contains high-stakes narrative evidence and the Shot describes an associated story action. Cinematic appearance alone is not used for this level.",
                confidence,
                evidence.Items);
        }

        AddSignals(evidence, "shot", shotNarrative, ImportantSignals, "narrative_importance");
        AddSignals(evidence, "scene", sceneNarrative, ImportantSignals, "narrative_importance");
        AddSignals(evidence, "story", storyNarrative, ImportantSignals, "narrative_importance");
        AddSignals(evidence, "guide", guideNarrative, ImportantSignals, "narrative_importance");

        var cameraText = Join(context.CameraAndFraming, context.CameraMotion, context.CinematographyJson);
        var hasCloseUp = HasAny(cameraText, "close-up", "closeup");
        if (hasCloseUp && HasAny(shotNarrative, EmotionalSignals))
        {
            evidence.Add("shot", "emotional_close_up", FirstMatchingExcerpt(shotNarrative, EmotionalSignals));
        }

        var importantEvidence = evidence.Items.Count(item => item.Signal == "narrative_importance" || item.Signal == "emotional_close_up");
        if (importantEvidence > 0)
        {
            var confidence = Math.Min(0.90m, 0.68m + (0.04m * Math.Min(importantEvidence, 4)));
            return new(
                MovieShotImportanceLevels.Important,
                "Classified as important because the Shot and/or its bounded Story/Scene/Guide context contains a narrative or emotionally meaningful beat. Camera language may support the finding but is not sufficient by itself.",
                confidence,
                evidence.Items);
        }

        AddSignals(evidence, "shot", shotNarrative, UtilitySignals, "utility_signal");
        var utilityEvidence = evidence.Items.Count(item => item.Signal == "utility_signal");
        if (utilityEvidence > 0)
        {
            return new(
                MovieShotImportanceLevels.UtilityBackground,
                "Classified as utility/background because the Shot is explicitly described as ambient, transitional, insert, or background material and no stronger narrative evidence was found in the supplied context.",
                Math.Min(0.86m, 0.74m + (0.04m * Math.Min(utilityEvidence, 3))),
                evidence.Items);
        }

        var contextSources = new[] { context.Story is not null, context.Guide is not null, !string.IsNullOrWhiteSpace(context.Scene.Title) }
            .Count(value => value);
        return new(
            MovieShotImportanceLevels.Standard,
            contextSources == 0
                ? "Classified as standard because no Story, Scene, or Guide context was available to support a stronger narrative classification."
                : "Classified as standard because no grounded high-stakes or narrative-importance cue was found across the supplied Shot, Scene, Story, and Guide context. Cinematic-looking fields do not raise importance on their own.",
            contextSources == 0 ? 0.42m : 0.56m,
            evidence.Items);
    }

    private static void AddSignals(EvidenceCollector evidence, string source, string text, IEnumerable<string> signals, string code)
    {
        foreach (var signal in signals)
        {
            if (text.IndexOf(signal, StringComparison.OrdinalIgnoreCase) < 0) continue;
            evidence.Add(source, code, Excerpt(text, signal));
        }
    }

    private static bool HasAny(string text, IEnumerable<string> signals) => signals.Any(signal =>
        text.IndexOf(signal, StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool HasAny(string text, params string[] signals) => HasAny(text, (IEnumerable<string>)signals);

    private static string FirstMatchingExcerpt(string text, IEnumerable<string> signals) =>
        signals.Select(signal => (signal, index: text.IndexOf(signal, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.index >= 0)
            .OrderBy(item => item.index)
            .Select(item => Excerpt(text, item.signal))
            .FirstOrDefault() ?? Excerpt(text, null);

    private static string Excerpt(string text, string? signal)
    {
        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 240) return normalized;
        var index = signal is null ? 0 : normalized.IndexOf(signal, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = 0;
        var start = Math.Max(0, index - 80);
        var length = Math.Min(240, normalized.Length - start);
        return (start > 0 ? "…" : string.Empty) + normalized.Substring(start, length).Trim() + (start + length < normalized.Length ? "…" : string.Empty);
    }

    private static string Join(params string?[] values) => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));

    private sealed class EvidenceCollector
    {
        private readonly List<MovieShotImportanceEvidenceDto> items = [];
        public IReadOnlyList<MovieShotImportanceEvidenceDto> Items => items;

        public void Add(string source, string signal, string excerpt)
        {
            if (string.IsNullOrWhiteSpace(excerpt) || items.Any(item => item.Source == source && item.Signal == signal && item.Excerpt == excerpt)) return;
            items.Add(new MovieShotImportanceEvidenceDto(source, signal, excerpt));
        }
    }
}

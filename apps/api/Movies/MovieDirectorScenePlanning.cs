using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class DirectorScenePlanActionTypes
{
    public const string PlanScenes = "plan_scenes";

    public static bool IsSupported(string? value) => string.Equals(value?.Trim(), PlanScenes, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) => IsSupported(value) ? PlanScenes : string.Empty;
}

public sealed record DirectorSceneProposal(
    int Sequence,
    string Title,
    string NarrativePurpose,
    string StoryBeat,
    string LocationEnvironment,
    string TimeOfDay,
    IReadOnlyList<string> ParticipatingCharacters,
    string EmotionalObjective,
    int EstimatedDurationSeconds,
    string TransitionRelationship,
    IReadOnlyList<string> ContinuityRequirements,
    string ProductionIntent,
    Guid? SourceScreenplaySceneId = null,
    Guid? ExistingMovieSceneId = null);

public sealed record DirectorScenePlanActionPayload(
    string Action,
    Guid? BaseRevisionId,
    string ContextSnapshotHash,
    string Language,
    string AspectRatio,
    int TargetDurationSeconds,
    int TotalEstimatedDurationSeconds,
    string QualityLevel,
    string AiCoreTier,
    decimal? EstimatedCostUsd,
    IReadOnlyList<DirectorSceneProposal> Scenes);

public sealed record DirectorScenePlanDto(
    Guid? BaseRevisionId,
    string Language,
    string AspectRatio,
    int TargetDurationSeconds,
    int TotalEstimatedDurationSeconds,
    string QualityLevel,
    IReadOnlyList<DirectorSceneProposal> Scenes);

public sealed record DirectorScenePlanReviewDto(
    string Action,
    Guid? BaseRevisionId,
    string Language,
    string AspectRatio,
    int TargetDurationSeconds,
    int TotalEstimatedDurationSeconds,
    string QualityLevel,
    IReadOnlyList<DirectorSceneProposal> Scenes,
    IReadOnlyList<string> GroundingNotes);

public sealed record DirectorScenePlanApplyResult(
    bool Applied,
    int TargetDurationSeconds,
    int TotalEstimatedDurationSeconds,
    IReadOnlyList<Guid> SceneIds,
    string SafeMessage);

public static class DirectorScenePlanCreativeFailureCodes
{
    public const string Unavailable = "DIRECTOR_SCENE_PLAN_CREATIVE_UNAVAILABLE";
    public const string Invalid = "DIRECTOR_SCENE_PLAN_CREATIVE_INVALID";
}

public sealed class DirectorScenePlanCreativeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieDirectorScenePlanningOptions
{
    public int MaxScenes { get; set; } = 24;
    public int MaxDurationSeconds { get; set; } = 3_600;
    public int MaxRepairAttempts { get; set; }
}

public static class DirectorScenePlanValidationReasonCodes
{
    public const string SchemaInvalid = "SCENE_PLAN_SCHEMA_INVALID";
    public const string RequiredFieldMissing = "SCENE_PLAN_REQUIRED_FIELD_MISSING";
    public const string RuntimeMismatch = "SCENE_PLAN_RUNTIME_MISMATCH";
    public const string TooManyScenes = "SCENE_PLAN_TOO_MANY_SCENES";
    public const string Ungrounded = "SCENE_PLAN_UNGROUNDED";
    public const string GuideUngrounded = "SCENE_PLAN_GUIDE_UNGROUNDED";
    public const string StoryUngrounded = "SCENE_PLAN_STORY_UNGROUNDED";
    public const string CharacterMismatch = "SCENE_PLAN_CHARACTER_MISMATCH";
    public const string InvalidSource = "SCENE_PLAN_INVALID_SOURCE";
    public const string Placeholder = "SCENE_PLAN_PLACEHOLDER";
    public const string LanguageMismatch = "SCENE_PLAN_LANGUAGE_MISMATCH";
    public const string BaseRevisionMismatch = "SCENE_PLAN_BASE_REVISION_MISMATCH";
    public const string ContextChanged = "SCENE_PLAN_CONTEXT_CHANGED";
}

public sealed record DirectorScenePlanValidationFinding(string ReasonCode, string Field);

public sealed record DirectorScenePlanValidationResult(
    bool IsValid,
    DirectorScenePlanActionPayload? Output,
    IReadOnlyList<DirectorScenePlanValidationFinding> Findings)
{
    public IReadOnlyList<string> ReasonCodes => Findings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal).ToArray();
}

public interface IMovieDirectorScenePlanValidator
{
    DirectorScenePlanValidationResult Validate(DirectorScenePlanActionPayload payload, DirectorStoryBoundedContextDto context, string? expectedContextHash = null);
}

public sealed class MovieDirectorScenePlanValidator(
    IOptions<MovieDirectorScenePlanningOptions> options,
    ILogger<MovieDirectorScenePlanValidator> logger) : IMovieDirectorScenePlanValidator
{
    private readonly MovieDirectorScenePlanningOptions settings = options.Value;

    public DirectorScenePlanValidationResult Validate(DirectorScenePlanActionPayload payload, DirectorStoryBoundedContextDto context, string? expectedContextHash = null)
    {
        var findings = new List<DirectorScenePlanValidationFinding>();
        if (payload is null || !DirectorScenePlanActionTypes.IsSupported(payload.Action))
        {
            findings.Add(new(DirectorScenePlanValidationReasonCodes.SchemaInvalid, "action"));
            return Rejected(findings);
        }

        var source = context.CurrentRevision ?? context.ApprovedRevision;
        var expectedRevision = source?.RevisionId;
        AddIf(findings, payload.BaseRevisionId != expectedRevision, DirectorScenePlanValidationReasonCodes.BaseRevisionMismatch, "baseRevisionId");
        AddIf(findings, string.IsNullOrWhiteSpace(payload.ContextSnapshotHash), DirectorScenePlanValidationReasonCodes.SchemaInvalid, "contextSnapshotHash");
        AddIf(findings, expectedContextHash is not null && !string.Equals(payload.ContextSnapshotHash, expectedContextHash, StringComparison.Ordinal), DirectorScenePlanValidationReasonCodes.ContextChanged, "contextSnapshotHash");
        AddIf(findings, !SupportedLanguage(context.Language, payload.Language), DirectorScenePlanValidationReasonCodes.LanguageMismatch, "language");
        AddIf(findings, string.IsNullOrWhiteSpace(payload.AspectRatio) || payload.AspectRatio.Length > 20 || !string.Equals(payload.AspectRatio, context.AspectRatio, StringComparison.OrdinalIgnoreCase), DirectorScenePlanValidationReasonCodes.SchemaInvalid, "aspectRatio");

        var targetDuration = Math.Clamp(context.DurationSeconds, 1, Math.Max(1, settings.MaxDurationSeconds));
        AddIf(findings, payload.TargetDurationSeconds != targetDuration, DirectorScenePlanValidationReasonCodes.RuntimeMismatch, "targetDurationSeconds");
        AddIf(findings, payload.Scenes is null || payload.Scenes.Count == 0, DirectorScenePlanValidationReasonCodes.RequiredFieldMissing, "scenes");
        if (payload.Scenes is null || payload.Scenes.Count == 0) return Rejected(findings);

        var maxScenes = targetDuration <= 30 ? Math.Min(settings.MaxScenes, 6) : Math.Min(settings.MaxScenes, 48);
        AddIf(findings, payload.Scenes.Count > maxScenes, DirectorScenePlanValidationReasonCodes.TooManyScenes, "scenes");
        AddIf(findings, payload.TotalEstimatedDurationSeconds != payload.Scenes.Sum(item => item.EstimatedDurationSeconds), DirectorScenePlanValidationReasonCodes.RuntimeMismatch, "totalEstimatedDurationSeconds");
        AddIf(findings, payload.TotalEstimatedDurationSeconds > targetDuration || payload.TotalEstimatedDurationSeconds < Math.Max(1, (int)Math.Ceiling(targetDuration * 0.8m)), DirectorScenePlanValidationReasonCodes.RuntimeMismatch, "totalEstimatedDurationSeconds");

        var screenplayScenes = AllScreenplayScenes(context);
        var movieSceneIds = context.RelevantMovieScenes.Select(item => item.Id).ToHashSet();
        var knownCharacters = KnownCharacters(context);
        var sequenceNumbers = new HashSet<int>();
        var combinedText = new List<string>();
        var storyText = new List<string>();
        var guideText = new List<string>();
        var continuityText = new List<string>();
        foreach (var revision in new[] { context.CurrentRevision, context.ApprovedRevision }.Where(item => item is not null).Select(item => item!))
        {
            storyText.AddRange([revision.Premise, revision.Logline, revision.Synopsis, revision.Treatment]);
            storyText.AddRange(revision.Scenes.SelectMany(scene => new[] { scene.Slugline, scene.Synopsis ?? string.Empty }.Concat(scene.Elements.Select(element => element.Content))));
        }
        guideText.AddRange([context.Guide.VisualLanguage, context.Guide.CameraLanguage, context.Guide.ColorAndLighting, context.Guide.SoundAndNarration, context.Guide.ContinuityRules]);
        guideText.AddRange(context.Guide.LockedSections?.Select(item => item.ContentJson) ?? []);
        continuityText.AddRange(context.RelevantCharacters.SelectMany(item => item.LockedFacts?.Select(fact => fact.LockedValue) ?? []));
        continuityText.AddRange(context.World?.Facts.Where(item => item.IsLocked).Select(item => item.FactValue) ?? []);
        continuityText.AddRange(context.World?.Locks.Select(item => item.LockedValue) ?? []);

        foreach (var scene in payload.Scenes.OrderBy(item => item.Sequence))
        {
            AddIf(findings, scene is null, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "scenes");
            if (scene is null) continue;
            AddIf(findings, scene.Sequence < 1 || !sequenceNumbers.Add(scene.Sequence), DirectorScenePlanValidationReasonCodes.SchemaInvalid, "sequence");
            ValidateText(findings, scene.Title, 160, "title");
            ValidateText(findings, scene.NarrativePurpose, 2_000, "narrativePurpose");
            ValidateText(findings, scene.StoryBeat, 4_000, "storyBeat");
            ValidateText(findings, scene.LocationEnvironment, 2_000, "locationEnvironment");
            ValidateText(findings, scene.TimeOfDay, 120, "timeOfDay");
            ValidateText(findings, scene.EmotionalObjective, 2_000, "emotionalObjective");
            ValidateText(findings, scene.TransitionRelationship, 1_000, "transitionRelationship");
            ValidateText(findings, scene.ProductionIntent, 4_000, "productionIntent");
            AddIf(findings, scene.EstimatedDurationSeconds < 1 || scene.EstimatedDurationSeconds > targetDuration, DirectorScenePlanValidationReasonCodes.RuntimeMismatch, "estimatedDurationSeconds");
            AddIf(findings, scene.ParticipatingCharacters is null, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "participatingCharacters");
            AddIf(findings, scene.ContinuityRequirements is null, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "continuityRequirements");
            AddIf(findings, scene.ParticipatingCharacters is not null && scene.ParticipatingCharacters.Count > 16, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "participatingCharacters");
            AddIf(findings, scene.ContinuityRequirements is not null && scene.ContinuityRequirements.Count > 16, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "continuityRequirements");
            if (scene.ParticipatingCharacters is not null)
            {
                foreach (var character in scene.ParticipatingCharacters)
                {
                    AddIf(findings, string.IsNullOrWhiteSpace(character) || character.Length > 160 || (knownCharacters.Count > 0 && !knownCharacters.Contains(Normalize(character))), DirectorScenePlanValidationReasonCodes.CharacterMismatch, "participatingCharacters");
                }
            }
            if (scene.SourceScreenplaySceneId is Guid screenplayId)
                AddIf(findings, screenplayScenes.All(item => item.Id != screenplayId), DirectorScenePlanValidationReasonCodes.InvalidSource, "sourceScreenplaySceneId");
            if (scene.ExistingMovieSceneId is Guid movieSceneId)
                AddIf(findings, !movieSceneIds.Contains(movieSceneId), DirectorScenePlanValidationReasonCodes.InvalidSource, "existingMovieSceneId");

            combinedText.AddRange([
                scene.Title, scene.NarrativePurpose, scene.StoryBeat, scene.LocationEnvironment,
                scene.TimeOfDay, scene.EmotionalObjective, scene.TransitionRelationship, scene.ProductionIntent,
                ..scene.ParticipatingCharacters ?? [], ..scene.ContinuityRequirements ?? []]);
        }

        var outputText = string.Join(" ", combinedText.Where(item => !string.IsNullOrWhiteSpace(item)));
        AddIf(findings, HasPlaceholder(outputText), DirectorScenePlanValidationReasonCodes.Placeholder, "scenes");
        AddIf(findings, !MatchesLanguage(outputText, context.Language), DirectorScenePlanValidationReasonCodes.LanguageMismatch, "scenes");
        AddIf(findings, !ContainsAnchor(outputText, storyText), DirectorScenePlanValidationReasonCodes.StoryUngrounded, "story");
        AddIf(findings, guideText.Any(value => !string.IsNullOrWhiteSpace(value)) && !ContainsAnchor(outputText, guideText), DirectorScenePlanValidationReasonCodes.GuideUngrounded, "guide");
        AddIf(findings, continuityText.Any(value => !string.IsNullOrWhiteSpace(value)) && !ContainsAnchor(outputText, continuityText), DirectorScenePlanValidationReasonCodes.Ungrounded, "continuity");
        AddIf(findings, storyText.Any(value => !string.IsNullOrWhiteSpace(value)) && !ContainsAnchor(outputText, context.MovieBrief, context.RelevantCharacters.Select(item => item.Name), context.RelevantWorldReferences.Select(item => item.Name)), DirectorScenePlanValidationReasonCodes.Ungrounded, "context");
        AddIf(findings, JsonSerializer.Serialize(payload, DirectorJson.Options).Length > 18_000, DirectorScenePlanValidationReasonCodes.SchemaInvalid, "payload");

        var result = findings.Count == 0 ? new DirectorScenePlanValidationResult(true, payload, []) : Rejected(findings);
        if (!result.IsValid)
            logger.LogWarning("Movie Director scene plan rejected. ReasonCodes={ReasonCodes}; FindingCount={FindingCount}", string.Join(',', result.ReasonCodes), findings.Count);
        return result;
    }

    private static IReadOnlyList<DirectorScreenplaySceneContext> AllScreenplayScenes(DirectorStoryBoundedContextDto context) =>
        (context.CurrentRevision?.Scenes ?? []).Concat(context.ApprovedRevision?.Scenes ?? []).GroupBy(item => item.Id).Select(item => item.First()).ToArray();

    private static IReadOnlySet<string> KnownCharacters(DirectorStoryBoundedContextDto context) =>
        context.RelevantCharacters.Select(item => Normalize(item.Name)).Where(item => item.Length > 0).Concat(
            AllScreenplayScenes(context).SelectMany(item => item.Elements).Where(item => !string.IsNullOrWhiteSpace(item.CharacterName)).Select(item => Normalize(item.CharacterName))).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsAnchor(string output, IEnumerable<string> values) =>
        ExtractAnchors(values).Any(anchor => output.Contains(anchor, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAnchor(string output, string brief, IEnumerable<string> characters, IEnumerable<string> locations) =>
        ContainsAnchor(output, new[] { brief }.Concat(characters).Concat(locations));

    private static IEnumerable<string> ExtractAnchors(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => Regex.Split(value!.Trim(), @"[^\p{L}\p{Nd}']+"))
        .Where(value => value.Length >= 4 && !StopWords.Contains(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool SupportedLanguage(string contextLanguage, string payloadLanguage) =>
        string.Equals(NormalizeLanguage(contextLanguage), NormalizeLanguage(payloadLanguage), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLanguage(string? value) => string.IsNullOrWhiteSpace(value) ? LanguageCodes.English : value.Trim().ToLowerInvariant();

    private static bool MatchesLanguage(string text, string? language)
    {
        var expected = NormalizeLanguage(language);
        var letters = text.Count(char.IsLetter);
        if (letters < 12) return true;
        var arabicLetters = text.Count(IsArabicLetter);
        var latinLetters = text.Count(IsLatinLetter);
        return expected switch
        {
            LanguageCodes.Arabic => arabicLetters >= Math.Max(4, (int)Math.Ceiling(letters * 0.15)),
            LanguageCodes.English => arabicLetters == 0 && latinLetters >= Math.Max(4, (int)Math.Ceiling(letters * 0.45)),
            LanguageCodes.Kurdish => true,
            _ => true,
        };
    }

    private static bool IsArabicLetter(char value) => value is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\u08A0' and <= '\u08FF';
    private static bool IsLatinLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || value is >= '\u00C0' and <= '\u024F';
    private static bool HasPlaceholder(string text) => new[] { "[insert", "{{", "}}", "lorem ipsum", "placeholder", "tbd", "todo:", "as an ai", "generated response" }.Any(item => text.Contains(item, StringComparison.OrdinalIgnoreCase));
    private static void ValidateText(List<DirectorScenePlanValidationFinding> findings, string? value, int maxLength, string field)
    {
        AddIf(findings, string.IsNullOrWhiteSpace(value), DirectorScenePlanValidationReasonCodes.RequiredFieldMissing, field);
        AddIf(findings, value?.Trim().Length > maxLength, DirectorScenePlanValidationReasonCodes.SchemaInvalid, field);
    }
    private static void AddIf(List<DirectorScenePlanValidationFinding> findings, bool condition, string code, string field) { if (condition && !findings.Any(item => item.ReasonCode == code && item.Field == field)) findings.Add(new(code, field)); }
    private static DirectorScenePlanValidationResult Rejected(IReadOnlyList<DirectorScenePlanValidationFinding> findings) => new(false, null, findings);
    private static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "must", "before", "after", "while", "when", "where", "story", "scene", "their", "they", "will", "then", "into", "guide", "locked", "canon", "visual", "camera", "time", "day", "night", "في", "من", "على", "هذا", "هذه", "و", "لە", "بۆ",
    };
}

public sealed record DirectorScenePlanAiGeneration(DirectorScenePlanActionPayload Payload, DirectorScenePlanReviewDto Review, string Title, string Summary, IReadOnlyList<string> Rationale, AiUsageMetadata? Usage);

public sealed class MovieDirectorScenePlanningAiService(
    IChatCompletionService completion,
    DirectorCreativeQualityPlanner qualityPlanner,
    IMovieDirectorScenePlanValidator validator)
{
    public async Task<DirectorScenePlanAiGeneration> BuildAsync(DirectorProposalRequest request, DirectorStoryBoundedContextDto context, string contextSnapshotHash, CancellationToken cancellationToken = default)
    {
        var profile = DirectorCreativeRoutingProfiles.For(DirectorCreativeTaskTypes.PlanScenes);
        var serializedContext = JsonSerializer.Serialize(context, DirectorJson.Options);
        var routing = qualityPlanner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.PlanScenes,
            request.Importance,
            request.Complexity,
            request.BudgetSensitivity,
            request.RequestedQuality,
            request.BudgetLimitUsd,
            Math.Clamp((serializedContext.Length + 3) / 4, 256, 64_000),
            profile.OutputTokens));
        var aiRequest = new AiChatRequest(
            [new AiChatMessage("user", JsonSerializer.Serialize(new
            {
                task = DirectorCreativeTaskTypes.PlanScenes,
                goal = request.Goal,
                targetDurationSeconds = context.DurationSeconds,
                aspectRatio = context.AspectRatio,
                language = context.Language,
                context,
            }, DirectorJson.Options))],
            "You are Taslim Movie Director. Return only the bounded JSON scene-plan schema. Convert the supplied approved/current Story and screenplay into a reviewable, duration-bounded scene plan. Ground every scene in the locked Guide, cast, world, brief, continuity facts, and screenplay. Preserve the project language, aspect ratio, and target duration. Do not invent provider metadata, placeholder text, or production scenes.",
            routing.AiCoreTier,
            MaxOutputTokens: profile.OutputTokens,
            JsonMode: true,
            StructuredOutput: DirectorScenePlanAiSchema.Spec);

        try
        {
            var generation = await completion.CompleteAsync(aiRequest, cancellationToken);
            if (!DirectorScenePlanAiDraft.TryParse(generation.Content, out var draft) || draft is null)
                throw Invalid("payload");
            var payload = ToPayload(draft, routing, context, contextSnapshotHash);
            var validation = validator.Validate(payload, context);
            if (!validation.IsValid || validation.Output is null)
                throw Invalid(string.Join(',', validation.ReasonCodes.Take(4)));
            payload = validation.Output;
            var review = new DirectorScenePlanReviewDto(payload.Action, payload.BaseRevisionId, payload.Language, payload.AspectRatio, payload.TargetDurationSeconds, payload.TotalEstimatedDurationSeconds, payload.QualityLevel, payload.Scenes, ["approved_or_current_story_and_screenplay", "locked_movie_guide", "bounded_cast_world_and_continuity", "target_runtime"]);
            return new(payload, review, "Plan scenes", $"Review a {payload.Scenes.Count}-scene plan bounded to the {payload.TargetDurationSeconds}-second movie target before applying it to production scenes.", ["ai_core_structured_output_validated", "bounded_story_screenplay_guide_cast_world_context", "runtime_validated", "explicit_approval_required_before_scene_apply"], generation.Usage);
        }
        catch (DirectorScenePlanCreativeException)
        {
            throw;
        }
        catch (AiProviderException exception) when (exception.FailureCategory == AiProviderFailureCategories.MalformedResponse)
        {
            throw Invalid("malformed_response");
        }
        catch (Exception exception) when (exception is AiProviderUnavailableException or AiProviderTimeoutException or AiGenerationException or MockAiProviderException)
        {
            throw new DirectorScenePlanCreativeException(DirectorScenePlanCreativeFailureCodes.Unavailable, "Scene-plan creative generation is currently unavailable. Please try again later.");
        }
    }

    private static DirectorScenePlanActionPayload ToPayload(DirectorScenePlanAiDraft draft, DirectorCreativeQualityRecommendation routing, DirectorStoryBoundedContextDto context, string contextSnapshotHash)
    {
        if (draft.Scenes is null || draft.Scenes.Count == 0) throw Invalid("scenes");
        var scenes = draft.Scenes.Select(item => new DirectorSceneProposal(
            item.Sequence,
            item.Title ?? string.Empty,
            item.NarrativePurpose ?? string.Empty,
            item.StoryBeat ?? string.Empty,
            item.LocationEnvironment ?? string.Empty,
            item.TimeOfDay ?? string.Empty,
            item.ParticipatingCharacters ?? [],
            item.EmotionalObjective ?? string.Empty,
            item.EstimatedDurationSeconds,
            item.TransitionRelationship ?? string.Empty,
            item.ContinuityRequirements ?? [],
            item.ProductionIntent ?? string.Empty,
            item.SourceScreenplaySceneId,
            item.ExistingMovieSceneId)).ToArray();
        return new(DirectorScenePlanActionTypes.PlanScenes, (context.CurrentRevision ?? context.ApprovedRevision)?.RevisionId, contextSnapshotHash, draft.Language ?? string.Empty, draft.AspectRatio ?? string.Empty, draft.TargetDurationSeconds, scenes.Sum(item => item.EstimatedDurationSeconds), routing.QualityLevel, routing.AiCoreTier, routing.EstimatedCostUsd, scenes);
    }

    private static DirectorScenePlanCreativeException Invalid(string detail) => new(DirectorScenePlanCreativeFailureCodes.Invalid, $"The scene-plan AI response was invalid or incomplete ({detail}). Please try again.");
}

public sealed class DirectorScenePlanAiDraft
{
    public string? Language { get; set; }
    public string? AspectRatio { get; set; }
    public int TargetDurationSeconds { get; set; }
    public List<DirectorScenePlanAiSceneDraft>? Scenes { get; set; }

    public static bool TryParse(string? content, out DirectorScenePlanAiDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(content)) return false;
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) json = json[(firstLine + 1)..lastFence].Trim();
        }
        try
        {
            draft = JsonSerializer.Deserialize<DirectorScenePlanAiDraft>(json, DirectorJson.Options);
            return draft is not null;
        }
        catch (JsonException) { return false; }
    }
}

public sealed class DirectorScenePlanAiSceneDraft
{
    public int Sequence { get; set; }
    public string? Title { get; set; }
    public string? NarrativePurpose { get; set; }
    public string? StoryBeat { get; set; }
    public string? LocationEnvironment { get; set; }
    public string? TimeOfDay { get; set; }
    public List<string>? ParticipatingCharacters { get; set; }
    public string? EmotionalObjective { get; set; }
    public int EstimatedDurationSeconds { get; set; }
    public string? TransitionRelationship { get; set; }
    public List<string>? ContinuityRequirements { get; set; }
    public string? ProductionIntent { get; set; }
    public Guid? SourceScreenplaySceneId { get; set; }
    public Guid? ExistingMovieSceneId { get; set; }
}

public static class DirectorScenePlanAiSchema
{
    public static readonly AiStructuredOutputSpec Spec = new(
        "taslim_movie_director_scene_plan",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                language = new { type = "string" },
                aspectRatio = new { type = "string" },
                targetDurationSeconds = new { type = "integer" },
                scenes = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            sequence = new { type = "integer" },
                            title = new { type = "string" },
                            narrativePurpose = new { type = "string" },
                            storyBeat = new { type = "string" },
                            locationEnvironment = new { type = "string" },
                            timeOfDay = new { type = "string" },
                            participatingCharacters = new { type = "array", items = new { type = "string" } },
                            emotionalObjective = new { type = "string" },
                            estimatedDurationSeconds = new { type = "integer" },
                            transitionRelationship = new { type = "string" },
                            continuityRequirements = new { type = "array", items = new { type = "string" } },
                            productionIntent = new { type = "string" },
                            sourceScreenplaySceneId = new { type = new[] { "string", "null" } },
                            existingMovieSceneId = new { type = new[] { "string", "null" } },
                        },
                    },
                },
            },
        }),
        Description: "Bounded Movie Director scene plan.",
        Strict: false);
}

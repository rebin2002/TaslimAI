using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class DirectorShotPlanningActionTypes
{
    public const string ProposeShots = "propose_shots";
    public const string RegenerateShots = "regenerate_shots";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ProposeShots, RegenerateShots,
    };
}

public sealed record DirectorShotProposalDto(
    int ShotNumber,
    string NarrativePurpose,
    int EstimatedDurationSeconds,
    string ShotSize,
    string Framing,
    string CameraAngle,
    string CameraMovement,
    string Subject,
    string CharacterAction,
    string ExpressionEmotionalState,
    string Environment,
    IReadOnlyList<string> ImportantProps,
    string Composition,
    string LightingIntent,
    string DepthBackgroundIntent,
    string TransitionRelationship,
    string DialogueAudioDependency,
    string ContinuityRequirements,
    string VfxRequirements,
    string ProductionNotes,
    IReadOnlyList<string> GroundingEvidence);

public sealed record DirectorShotPlanReviewDto(
    Guid SceneId,
    string SceneTitle,
    string ContextHash,
    int? SceneDurationSeconds,
    int ExistingActiveDurationSeconds,
    int ProposedDurationSeconds,
    bool RuntimeCompatible,
    bool IsRegeneration);

public sealed record MovieShotPlanningContext(
    DirectorContextDto DirectorContext,
    DirectorSceneContext SelectedScene,
    IReadOnlyList<DirectorSceneContext> SurroundingScenes,
    string SnapshotJson,
    string SnapshotHash,
    int ExistingActiveDurationSeconds);

public sealed record DirectorShotPlanningPayload(
    Guid MovieSceneId,
    string ContextHash,
    int? SceneDurationSeconds,
    int ProjectDurationSeconds,
    int ExistingActiveDurationSeconds,
    bool IsRegeneration,
    string QualityLevel,
    IReadOnlyList<DirectorShotProposalDto> Shots);

public sealed record DirectorShotPlanningAiGeneration(
    string Summary,
    IReadOnlyList<string> Rationale,
    IReadOnlyList<DirectorShotProposalDto> Shots,
    DirectorCreativeQualityRecommendation Routing,
    AiUsageMetadata? Usage);

public sealed class DirectorShotPlanningDraft
{
    public string? Summary { get; set; }
    public List<string>? Rationale { get; set; }
    public List<DirectorShotProposalDraft>? Shots { get; set; }
}

public sealed class DirectorShotProposalDraft
{
    public int ShotNumber { get; set; }
    public string? NarrativePurpose { get; set; }
    public int EstimatedDurationSeconds { get; set; }
    public string? ShotSize { get; set; }
    public string? Framing { get; set; }
    public string? CameraAngle { get; set; }
    public string? CameraMovement { get; set; }
    public string? Subject { get; set; }
    public string? CharacterAction { get; set; }
    public string? ExpressionEmotionalState { get; set; }
    public string? Environment { get; set; }
    public List<string>? ImportantProps { get; set; }
    public string? Composition { get; set; }
    public string? LightingIntent { get; set; }
    public string? DepthBackgroundIntent { get; set; }
    public string? TransitionRelationship { get; set; }
    public string? DialogueAudioDependency { get; set; }
    public string? ContinuityRequirements { get; set; }
    public string? VfxRequirements { get; set; }
    public string? ProductionNotes { get; set; }
    public List<string>? GroundingEvidence { get; set; }
}

public static class DirectorShotPlanningStructuredOutput
{
    public static readonly AiStructuredOutputSpec Spec = new(
        "taslim_movie_director_shot_planning",
        JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "summary": { "type": "string" },
            "rationale": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 8 },
            "shots": {
              "type": "array", "minItems": 1, "maxItems": 12,
              "items": {
                "type": "object", "additionalProperties": false,
                "properties": {
                  "shotNumber": { "type": "integer" },
                  "narrativePurpose": { "type": "string" },
                  "estimatedDurationSeconds": { "type": "integer" },
                  "shotSize": { "type": "string" },
                  "framing": { "type": "string" },
                  "cameraAngle": { "type": "string" },
                  "cameraMovement": { "type": "string" },
                  "subject": { "type": "string" },
                  "characterAction": { "type": "string" },
                  "expressionEmotionalState": { "type": "string" },
                  "environment": { "type": "string" },
                  "importantProps": { "type": "array", "items": { "type": "string" }, "maxItems": 12 },
                  "composition": { "type": "string" },
                  "lightingIntent": { "type": "string" },
                  "depthBackgroundIntent": { "type": "string" },
                  "transitionRelationship": { "type": "string" },
                  "dialogueAudioDependency": { "type": "string" },
                  "continuityRequirements": { "type": "string" },
                  "vfxRequirements": { "type": "string" },
                  "productionNotes": { "type": "string" },
                  "groundingEvidence": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 6 }
                },
                "required": ["shotNumber", "narrativePurpose", "estimatedDurationSeconds", "shotSize", "framing", "cameraAngle", "cameraMovement", "subject", "characterAction", "expressionEmotionalState", "environment", "importantProps", "composition", "lightingIntent", "depthBackgroundIntent", "transitionRelationship", "dialogueAudioDependency", "continuityRequirements", "vfxRequirements", "productionNotes", "groundingEvidence"]
              }
            }
          },
          "required": ["summary", "rationale", "shots"]
        }
        """).RootElement.Clone(),
        "A production-ready, canon-grounded Movie shot plan. Every shot remains proposed until a human explicitly applies it.");
}

public static class DirectorShotPlanningFailureCodes
{
    public const string Unavailable = "DIRECTOR_SHOT_PLANNING_UNAVAILABLE";
    public const string Invalid = "DIRECTOR_SHOT_PLANNING_INVALID";
    public const string Stale = "DIRECTOR_SHOT_PLANNING_STALE";
}

public sealed class DirectorShotPlanningException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class MovieShotPlanningContextAssembler(
    TaslimDbContext db,
    MovieDirectorContextAssembler directorContextAssembler)
{
    public async Task<MovieShotPlanningContext?> AssembleAsync(
        Guid userId,
        Guid movieProjectId,
        Guid sceneId,
        CancellationToken cancellationToken = default)
    {
        var assembled = await directorContextAssembler.AssembleAsync(
            userId,
            movieProjectId,
            new DirectorContextTargetRequest { TargetType = DirectorContextTargetTypes.Scene, TargetId = sceneId },
            cancellationToken);
        if (assembled is null) return null;
        var selected = assembled.Context.Scenes.FirstOrDefault(scene => scene.Id == sceneId);
        if (selected is null) throw new DirectorContextTargetException("The shot-planning scene is not part of this movie project.");

        var surrounding = await db.MovieScenes.AsNoTracking()
            .Where(scene => scene.MovieProjectId == movieProjectId && scene.Id != sceneId)
            .OrderBy(scene => Math.Abs(scene.Sequence - selected.Sequence))
            .ThenBy(scene => scene.Sequence)
            .Take(2)
            .Select(scene => new DirectorSceneContext(
                scene.Id,
                scene.Sequence,
                scene.Title,
                scene.Summary,
                scene.DurationSeconds,
                scene.ContinuityNotes,
                Array.Empty<DirectorShotContext>()))
            .ToListAsync(cancellationToken);

        var existingDuration = selected.Shots
            .Where(shot => shot.DurationSeconds is > 0)
            .Sum(shot => shot.DurationSeconds!.Value);
        var snapshot = JsonSerializer.Serialize(new
        {
            directorContext = assembled.Context,
            selectedScene = selected,
            surroundingScenes = surrounding,
            existingActiveDurationSeconds = existingDuration,
        }, DirectorJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        return new MovieShotPlanningContext(assembled.Context, selected, surrounding, snapshot, hash, existingDuration);
    }
}

public sealed class MovieShotPlanningAiService(
    IChatCompletionService completion,
    DirectorCreativeQualityPlanner qualityPlanner)
{
    public async Task<DirectorShotPlanningAiGeneration> BuildAsync(
        DirectorProposalRequest request,
        MovieShotPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var action = NormalizeAction(request.ShotPlanningAction);
        var serializedContext = JsonSerializer.Serialize(context, DirectorJson.Options);
        var profile = DirectorCreativeRoutingProfiles.For(DirectorCreativeTaskTypes.PlanShots);
        var routing = qualityPlanner.Recommend(new DirectorCreativeRoutingRequest(
            DirectorCreativeTaskTypes.PlanShots,
            request.Importance,
            request.Complexity,
            request.BudgetSensitivity,
            request.RequestedQuality,
            request.BudgetLimitUsd,
            Math.Clamp((serializedContext.Length + 3) / 4, 256, 64_000),
            profile.OutputTokens));
        var requestedCount = request.RequestedShotCount is int count ? Math.Clamp(count, 1, 12) : (int?)null;
        var maximumRuntime = Math.Min(
            context.DirectorContext.DurationSeconds,
            context.SelectedScene.DurationSeconds ?? context.DirectorContext.DurationSeconds);
        var user = $"""
Create a {action} for the selected Movie scene.
Return only the bounded JSON object described by the schema. Produce {requestedCount?.ToString() ?? "the smallest complete number of"} production-ready shots, never a generic template.
The parent scene runtime is {maximumRuntime} seconds and {context.ExistingActiveDurationSeconds} seconds are already allocated by existing shots. The total proposed duration must fit the remaining scene runtime.
Use the selected scene as the primary narrative source. Use surrounding scenes only for transition and continuity relationships. Use locked Guide, approved Story, Cast, World, screenplay, aspect ratio, quality tier, and continuity facts as authoritative constraints.
Every groundingEvidence entry must quote or closely name a supplied fact. New creative details must be clearly marked as proposed in productionNotes; do not assert them as locked canon.
This is planning only: do not request images, video, rendering, providers, or generation jobs.
BOUNDED CONTEXT JSON:
{serializedContext}
""";
        var aiRequest = new AiChatRequest(
            [new AiChatMessage("user", user)],
            "You are Taslim Movie Director's shot-planning intelligence. Preserve canon and locked continuity. Return complete structured shot data only. Never output deterministic filler such as 'Opening shot', 'wide establishing shot', or 'Character walks into frame'. If the context is insufficient, fail rather than inventing a fake fallback.",
            routing.AiCoreTier,
            EnableStreaming: false,
            MaxOutputTokens: profile.OutputTokens,
            JsonMode: true,
            StructuredOutput: DirectorShotPlanningStructuredOutput.Spec);
        AiGenerationResult generation;
        try
        {
            generation = await completion.CompleteAsync(aiRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Unavailable, "Shot planning AI timed out.");
        }
        catch (AiProviderException exception) when (exception.FailureCategory == AiProviderFailureCategories.MalformedResponse)
        {
            throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Invalid, "Shot planning AI returned malformed structured output.", exception);
        }
        catch (Exception exception) when (exception is AiProviderUnavailableException or AiProviderTimeoutException or AiGenerationException or MockAiProviderException)
        {
            throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Unavailable, "Shot planning AI is currently unavailable.", exception);
        }

        DirectorShotPlanningDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<DirectorShotPlanningDraft>(generation.Content, DirectorJson.Options)
                ?? throw new JsonException("Empty shot plan.");
        }
        catch (JsonException exception)
        {
            throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Invalid, "Shot planning AI returned invalid structured output.", exception);
        }
        var shots = DirectorShotPlanningValidator.ValidateAndNormalize(draft, context, requestedCount);
        return new(
            CleanRequired(draft.Summary, "summary"),
            (draft.Rationale ?? []).Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
            shots,
            routing,
            generation.Usage);
    }

    public static string NormalizeAction(string? value) =>
        DirectorShotPlanningActionTypes.Supported.FirstOrDefault(item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new DirectorValidationException("Choose a supported Movie shot-planning action.");

    private static string CleanRequired(string? value, string field) => string.IsNullOrWhiteSpace(value)
        ? throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Invalid, $"Shot planning AI omitted {field}.")
        : value.Trim();
}

public static class DirectorShotPlanningValidator
{
    private static readonly string[] FakePhrases =
    [
        "opening shot",
        "wide establishing shot",
        "character walks into frame",
    ];

    public static IReadOnlyList<DirectorShotProposalDto> ValidateAndNormalize(
        DirectorShotPlanningDraft draft,
        MovieShotPlanningContext context,
        int? requestedCount = null) =>
        ValidateAndNormalize(
            draft.Shots?.Select(ToDto).ToArray() ?? [],
            context,
            requestedCount,
            draft.Rationale);

    public static IReadOnlyList<DirectorShotProposalDto> ValidateAndNormalize(
        IReadOnlyList<DirectorShotProposalDto> shots,
        MovieShotPlanningContext context,
        int? requestedCount = null,
        IReadOnlyList<string>? rationale = null)
    {
        if (shots.Count is < 1 or > 12)
            Invalid("The shot plan must contain between 1 and 12 shots.");
        if (requestedCount.HasValue && shots.Count != requestedCount.Value)
            Invalid($"The shot plan must contain exactly {requestedCount.Value} shots.");
        if (rationale is null || rationale.Count == 0 || rationale.Any(string.IsNullOrWhiteSpace))
            Invalid("The shot plan must explain its grounded creative rationale.");

        var ordered = shots.OrderBy(item => item.ShotNumber).ToArray();
        if (ordered.Select((shot, index) => shot.ShotNumber == index + 1).Any(isSequential => !isSequential))
            Invalid("Shot numbers must be sequential starting at 1.");

        var maximumRuntime = Math.Min(
            context.DirectorContext.DurationSeconds,
            context.SelectedScene.DurationSeconds ?? context.DirectorContext.DurationSeconds);
        if (maximumRuntime <= 0) Invalid("The selected scene has no usable runtime budget.");
        var remaining = maximumRuntime - context.ExistingActiveDurationSeconds;
        if (remaining <= 0) Invalid("The selected scene has no remaining runtime for a new shot plan.");
        var total = 0;
        foreach (var shot in ordered)
        {
            var fields = new[]
            {
                shot.NarrativePurpose, shot.ShotSize, shot.Framing, shot.CameraAngle, shot.CameraMovement,
                shot.Subject, shot.CharacterAction, shot.ExpressionEmotionalState, shot.Environment,
                shot.Composition, shot.LightingIntent, shot.DepthBackgroundIntent, shot.TransitionRelationship,
                shot.DialogueAudioDependency, shot.ContinuityRequirements, shot.VfxRequirements, shot.ProductionNotes,
            };
            if (fields.Any(string.IsNullOrWhiteSpace)) Invalid($"Shot {shot.ShotNumber} omitted a production-ready field.");
            if (shot.EstimatedDurationSeconds is < 1 or > 3_600) Invalid($"Shot {shot.ShotNumber} has an invalid duration.");
            if (shot.ImportantProps is null || shot.ImportantProps.Count > 12 || shot.ImportantProps.Any(string.IsNullOrWhiteSpace)) Invalid($"Shot {shot.ShotNumber} has invalid important props.");
            if (shot.GroundingEvidence is null || shot.GroundingEvidence.Count == 0 || shot.GroundingEvidence.Count > 6 || shot.GroundingEvidence.Any(string.IsNullOrWhiteSpace)) Invalid($"Shot {shot.ShotNumber} is missing grounding evidence.");
            if (ContainsFakePhrase(shot)) Invalid($"Shot {shot.ShotNumber} contains generic deterministic filler.");
            var groundingEvidence = shot.GroundingEvidence ?? [];
            if (groundingEvidence.Any(evidence => !GroundingCorpus(context).Contains(evidence.Trim(), StringComparison.OrdinalIgnoreCase)))
                Invalid($"Shot {shot.ShotNumber} cites grounding evidence that is not present in the supplied context.");
            total += shot.EstimatedDurationSeconds;
        }
        if (total > remaining) Invalid($"The proposed shot duration of {total} seconds exceeds the {remaining}-second scene runtime remaining.");
        return ordered.Select(Normalize).ToArray();
    }

    public static string GroundingCorpus(MovieShotPlanningContext context) =>
        JsonSerializer.Serialize(new
        {
            context.DirectorContext.Title,
            context.DirectorContext.Description,
            context.DirectorContext.AspectRatio,
            context.DirectorContext.Style,
            context.DirectorContext.Guide,
            context.DirectorContext.ApprovedStory,
            context.DirectorContext.Scenes,
            context.DirectorContext.Characters,
            context.DirectorContext.Locations,
            context.DirectorContext.World,
            context.DirectorContext.Continuity,
            context.SelectedScene,
            context.SurroundingScenes,
        }, DirectorJson.Options);

    private static DirectorShotProposalDto ToDto(DirectorShotProposalDraft draft) => new(
        draft.ShotNumber,
        draft.NarrativePurpose ?? string.Empty,
        draft.EstimatedDurationSeconds,
        draft.ShotSize ?? string.Empty,
        draft.Framing ?? string.Empty,
        draft.CameraAngle ?? string.Empty,
        draft.CameraMovement ?? string.Empty,
        draft.Subject ?? string.Empty,
        draft.CharacterAction ?? string.Empty,
        draft.ExpressionEmotionalState ?? string.Empty,
        draft.Environment ?? string.Empty,
        draft.ImportantProps ?? [],
        draft.Composition ?? string.Empty,
        draft.LightingIntent ?? string.Empty,
        draft.DepthBackgroundIntent ?? string.Empty,
        draft.TransitionRelationship ?? string.Empty,
        draft.DialogueAudioDependency ?? string.Empty,
        draft.ContinuityRequirements ?? string.Empty,
        draft.VfxRequirements ?? string.Empty,
        draft.ProductionNotes ?? string.Empty,
        draft.GroundingEvidence ?? []);

    private static DirectorShotProposalDto Normalize(DirectorShotProposalDto shot) => shot with
    {
        NarrativePurpose = shot.NarrativePurpose.Trim(),
        ShotSize = shot.ShotSize.Trim(),
        Framing = shot.Framing.Trim(),
        CameraAngle = shot.CameraAngle.Trim(),
        CameraMovement = shot.CameraMovement.Trim(),
        Subject = shot.Subject.Trim(),
        CharacterAction = shot.CharacterAction.Trim(),
        ExpressionEmotionalState = shot.ExpressionEmotionalState.Trim(),
        Environment = shot.Environment.Trim(),
        ImportantProps = shot.ImportantProps.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
        Composition = shot.Composition.Trim(),
        LightingIntent = shot.LightingIntent.Trim(),
        DepthBackgroundIntent = shot.DepthBackgroundIntent.Trim(),
        TransitionRelationship = shot.TransitionRelationship.Trim(),
        DialogueAudioDependency = shot.DialogueAudioDependency.Trim(),
        ContinuityRequirements = shot.ContinuityRequirements.Trim(),
        VfxRequirements = shot.VfxRequirements.Trim(),
        ProductionNotes = shot.ProductionNotes.Trim(),
        GroundingEvidence = shot.GroundingEvidence.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
    };

    private static bool ContainsFakePhrase(DirectorShotProposalDto shot)
    {
        var text = string.Join(" ", shot.NarrativePurpose, shot.CharacterAction, shot.ProductionNotes, shot.Composition);
        return FakePhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static void Invalid(string message) => throw new DirectorShotPlanningException(DirectorShotPlanningFailureCodes.Invalid, message);
}

public sealed class MovieShotPlanningActionExecutor(
    TaslimDbContext db,
    MovieShotPlanningContextAssembler contextAssembler,
    MovieAuthorizationService authorization) : IDirectorActionExecutor
{
    public string ActionType => DirectorShotPlanningActionTypes.ProposeShots;

    public async Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, Guid executingUserId, CancellationToken cancellationToken = default)
    {
        DirectorShotPlanningPayload? payload;
        try { payload = JsonSerializer.Deserialize<DirectorShotPlanningPayload>(action.PayloadJson, DirectorJson.Options); }
        catch (JsonException) { payload = null; }
        if (payload is null || payload.Shots.Count == 0)
            return new(false, DirectorShotPlanningFailureCodes.Invalid, "The shot-planning proposal payload is invalid.", null);
        if (!await authorization.CanAsync(executingUserId, action.MovieProjectId, MovieOperationalActions.ShotEdit, cancellationToken))
            return new(false, "DIRECTOR_SHOT_PLAN_UNAUTHORIZED", "The shot plan can no longer be applied by this user.", null);

        var current = await contextAssembler.AssembleAsync(executingUserId, action.MovieProjectId, payload.MovieSceneId, cancellationToken);
        if (current is null)
            return new(false, "DIRECTOR_SCENE_NOT_FOUND", "The shot-planning scene could not be found.", null);
        if (!string.Equals(current.SnapshotHash, payload.ContextHash, StringComparison.Ordinal))
            return new(false, DirectorShotPlanningFailureCodes.Stale, "The shot plan is stale because the selected scene or its grounded context changed. Generate a new proposal.", null);

        try
        {
            DirectorShotPlanningValidator.ValidateAndNormalize(payload.Shots, current, payload.Shots.Count, ["approved grounded shot plan"]);
        }
        catch (DirectorShotPlanningException exception)
        {
            return new(false, exception.Code, "The shot plan is no longer valid for the current scene runtime or canon.", null);
        }

        var scene = await db.MovieScenes.FirstOrDefaultAsync(item => item.Id == payload.MovieSceneId && item.MovieProjectId == action.MovieProjectId, cancellationToken);
        if (scene is null) return new(false, "DIRECTOR_SCENE_NOT_FOUND", "The shot-planning scene could not be found.", null);
        var now = DateTime.UtcNow;
        var nextSequence = await db.MovieShots.Where(item => item.MovieSceneId == scene.Id).Select(item => (int?)item.Sequence).MaxAsync(cancellationToken) ?? 0;
        var created = new List<MovieShot>();
        foreach (var proposal in payload.Shots.OrderBy(item => item.ShotNumber))
        {
            var productionRequirements = $"Important props: {string.Join(", ", proposal.ImportantProps)}. VFX: {proposal.VfxRequirements}. Production notes: {proposal.ProductionNotes}";
            var shot = new MovieShot
            {
                Id = Guid.NewGuid(),
                MovieSceneId = scene.Id,
                Sequence = ++nextSequence,
                Description = proposal.CharacterAction,
                Purpose = proposal.NarrativePurpose,
                Subjects = proposal.Subject,
                LocationSet = proposal.Environment,
                DurationSeconds = proposal.EstimatedDurationSeconds,
                ProductionRequirements = productionRequirements,
                ContinuityReferences = $"{proposal.ContinuityRequirements} Grounding: {string.Join("; ", proposal.GroundingEvidence)}",
                CameraAndFraming = $"{proposal.ShotSize}; {proposal.Framing}; {proposal.CameraAngle}",
                CameraMotion = proposal.CameraMovement,
                CinematographyJson = JsonSerializer.Serialize(new
                {
                    shotSize = proposal.ShotSize,
                    framing = proposal.Framing,
                    cameraAngle = proposal.CameraAngle,
                    cameraMovement = proposal.CameraMovement,
                    composition = proposal.Composition,
                    lightingIntent = proposal.LightingIntent,
                    depthBackgroundIntent = proposal.DepthBackgroundIntent,
                    transitionRelationship = proposal.TransitionRelationship,
                    expressionEmotionalState = proposal.ExpressionEmotionalState,
                    vfxRequirements = proposal.VfxRequirements,
                    source = "movie_director_shot_planning",
                }, DirectorJson.Options),
                Dialogue = proposal.DialogueAudioDependency,
                VisualContinuityNotes = $"{proposal.ExpressionEmotionalState} Transition: {proposal.TransitionRelationship}",
                Status = MovieShotStatuses.Planned,
                CreatedAt = now,
                UpdatedAt = now,
            };
            created.Add(shot);
        }
        db.MovieShots.AddRange(created);
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, $"Applied {created.Count} proposed shots to the scene. No media generation was started.", JsonSerializer.Serialize(new { sceneId = scene.Id, shotIds = created.Select(item => item.Id).ToArray(), planningOnly = true }, DirectorJson.Options));
    }
}

public sealed class MovieShotPlanningRegenerationActionExecutor(
    TaslimDbContext db,
    MovieShotPlanningContextAssembler contextAssembler,
    MovieAuthorizationService authorization) : IDirectorActionExecutor
{
    private readonly MovieShotPlanningActionExecutor inner = new(db, contextAssembler, authorization);
    public string ActionType => DirectorShotPlanningActionTypes.RegenerateShots;
    public Task<DirectorActionExecution> ExecuteAsync(DirectorAction action, Guid executingUserId, CancellationToken cancellationToken = default) => inner.ExecuteAsync(action, executingUserId, cancellationToken);
}

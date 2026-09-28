using System.Text.Json;
using Taslim.Api.Ai;

namespace Taslim.Api.Movies;

public static class MovieSynopsisScopes
{
    public const string Short = "short";
    public const string Medium = "medium";
    public const string Feature = "feature";

    public static MovieSynopsisScopeRules ForDuration(int durationSeconds)
    {
        var bounded = Math.Max(1, durationSeconds);
        return bounded <= 900
            ? new(Short, bounded, 4, 1, 180)
            : bounded <= 3_600
                ? new(Medium, bounded, 8, 3, 360)
                : new(Feature, bounded, 12, 5, 700);
    }
}

public sealed record MovieSynopsisScopeRules(
    string Scope,
    int DurationSeconds,
    int MaximumBeats,
    int MaximumComplications,
    int MaximumSynopsisWords);

/// <summary>
/// The structured proposal returned by the AI. It is never a Story revision by itself.
/// </summary>
public sealed record MovieSynopsisDevelopmentDraft(
    string Premise,
    string Logline,
    string Treatment,
    string Synopsis,
    string Setup,
    string ProtagonistMotivation,
    string IncitingEvent,
    string Escalation,
    IReadOnlyList<string> Complications,
    string ClimaxChoice,
    string Resolution,
    string EmotionalArc,
    IReadOnlyList<string> CanonAnchors,
    IReadOnlyList<string> ProposedElements,
    int BeatCount);

/// <summary>
/// Public review shape. Status is deliberately explicit so proposed material cannot be mistaken for canon.
/// </summary>
public sealed record MovieSynopsisDevelopmentDto(
    string Status,
    string Scope,
    int DurationSeconds,
    string Synopsis,
    string Setup,
    string ProtagonistMotivation,
    string IncitingEvent,
    string Escalation,
    IReadOnlyList<string> Complications,
    string ClimaxChoice,
    string Resolution,
    string EmotionalArc,
    IReadOnlyList<string> CanonAnchors,
    IReadOnlyList<string> ProposedElements,
    int BeatCount);

public interface IMovieSynopsisDevelopmentService
{
    Task<MovieSynopsisDevelopmentDraft> GenerateAsync(
        DirectorStoryBoundedContextDto context,
        DirectorStoryRevisionContext? source,
        CancellationToken cancellationToken = default);
}

public sealed class AiMovieSynopsisDevelopmentService(IChatCompletionService completion) : IMovieSynopsisDevelopmentService
{
    public async Task<MovieSynopsisDevelopmentDraft> GenerateAsync(
        DirectorStoryBoundedContextDto context,
        DirectorStoryRevisionContext? source,
        CancellationToken cancellationToken = default)
    {
        var rules = MovieSynopsisScopes.ForDuration(context.DurationSeconds);
        var sourceJson = JsonSerializer.Serialize(new
        {
            durationSeconds = context.DurationSeconds,
            scope = rules.Scope,
            movieBrief = context.MovieBrief,
            lockedGuide = context.Guide,
            currentStory = context.CurrentRevision,
            approvedStory = context.ApprovedRevision,
            characters = context.RelevantCharacters,
            locations = context.RelevantWorldReferences,
        }, DirectorJson.Options);
        var system = $"""
You are Taslim's movie story architect. Develop a coherent synopsis proposal, not a screenplay.
The requested runtime is {rules.DurationSeconds} seconds and the scope is {rules.Scope}.
Return only the requested structured object. Every field is proposed material until a human approves the Director proposal.
Use the current story as the working foundation when present, while treating the approved story and locked guide as canon constraints.
Never contradict, replace, or silently reinterpret locked or approved facts. If a necessary detail is absent, make the smallest useful creative proposal and list it in proposedElements.
The narrative must causally connect setup, protagonist motivation, inciting event, escalation, meaningful complications, climax choice, resolution, and emotional arc.
Respect the runtime: a short story has one central turn and at most {rules.MaximumComplications} meaningful complication; do not invent a feature-scale subplot or ensemble.
Write synopsis prose only, with no screenplay formatting, sluglines, scene headings, shot directions, dialogue blocks, or scene-by-scene list.
""";
        var user = $"""
Create or refine the synopsis from this bounded project context.
For an empty story, create the minimum complete foundation needed for this runtime. For an existing story, preserve its premise, logline, treatment, and canon while improving only the narrative synopsis.
The synopsis should be approximately {Math.Max(40, rules.MaximumSynopsisWords / 2)} to {rules.MaximumSynopsisWords} words and have no more than {rules.MaximumBeats} causal beats.
Mark all new details as proposed through proposedElements; canonAnchors should name the facts you relied on.

BOUNDED CONTEXT JSON:
{sourceJson}
""";
        AiGenerationResult result;
        try
        {
            result = await completion.CompleteAsync(
                new AiChatRequest(
                    [new AiChatMessage("user", user)],
                    system,
                    RequestedTier: "Smart",
                    EnableStreaming: false,
                    MaxOutputTokens: 2_400,
                    StructuredOutput: MovieSynopsisDevelopmentStructuredOutput.Spec),
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DirectorSynopsisGenerationException("The synopsis development provider timed out.");
        }
        catch (AiProviderException exception)
        {
            throw new DirectorSynopsisGenerationException("The synopsis development provider could not produce a proposal.", exception);
        }
        catch (AiProviderUnavailableException exception)
        {
            throw new DirectorSynopsisGenerationException("The synopsis development provider is not configured.", exception);
        }
        catch (AiGenerationException exception)
        {
            throw new DirectorSynopsisGenerationException("The synopsis development provider could not produce a proposal.", exception);
        }
        if (string.IsNullOrWhiteSpace(result.Content))
            throw new DirectorSynopsisGenerationException("The synopsis development provider returned an empty proposal.");
        MovieSynopsisDevelopmentDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<MovieSynopsisDevelopmentDraft>(result.Content, DirectorJson.Options)
                ?? throw new JsonException("Empty synopsis draft.");
        }
        catch (JsonException exception)
        {
            throw new DirectorSynopsisGenerationException("The synopsis development provider returned invalid structured output.", exception);
        }
        Validate(draft, context, rules);
        return draft;
    }

    private static void Validate(MovieSynopsisDevelopmentDraft draft, DirectorStoryBoundedContextDto context, MovieSynopsisScopeRules rules)
    {
        var fields = new[]
        {
            draft.Premise, draft.Logline, draft.Treatment, draft.Synopsis, draft.Setup,
            draft.ProtagonistMotivation, draft.IncitingEvent, draft.Escalation,
            draft.ClimaxChoice, draft.Resolution, draft.EmotionalArc,
        };
        if (fields.Any(string.IsNullOrWhiteSpace))
            throw new DirectorSynopsisGenerationException("The synopsis proposal omitted a required narrative section.");
        if (draft.BeatCount is < 1 || draft.BeatCount > rules.MaximumBeats)
            throw new DirectorSynopsisGenerationException($"The synopsis proposal exceeds the {rules.Scope} runtime beat limit.");
        if (draft.Complications.Count is < 1 || draft.Complications.Count > rules.MaximumComplications)
            throw new DirectorSynopsisGenerationException($"The synopsis proposal exceeds the {rules.Scope} runtime complication limit.");
        var wordCount = draft.Synopsis.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > rules.MaximumSynopsisWords)
            throw new DirectorSynopsisGenerationException($"The synopsis proposal is too large for a {rules.Scope} runtime.");
        if (ContainsScreenplayFormatting(draft.Synopsis) || ContainsScreenplayFormatting(string.Join(" ", fields)))
            throw new DirectorSynopsisGenerationException("Synopsis development must not contain screenplay formatting.");
        if (draft.ProposedElements.Count == 0)
            throw new DirectorSynopsisGenerationException("New creative material must be explicitly listed as proposed.");
        if (draft.CanonAnchors.Count == 0 && (context.CurrentRevision is not null || context.ApprovedRevision is not null))
            throw new DirectorSynopsisGenerationException("The synopsis proposal must identify the canon it preserves.");
    }

    private static bool ContainsScreenplayFormatting(string value) =>
        value.Contains("INT.", StringComparison.OrdinalIgnoreCase)
        || value.Contains("EXT.", StringComparison.OrdinalIgnoreCase)
        || value.Contains("FADE IN", StringComparison.OrdinalIgnoreCase)
        || value.Contains("FADE OUT", StringComparison.OrdinalIgnoreCase);
}

public static class MovieSynopsisDevelopmentStructuredOutput
{
    public static AiStructuredOutputSpec Spec { get; } = new(
        "taslim_movie_synopsis_development",
        JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "premise": { "type": "string" },
            "logline": { "type": "string" },
            "treatment": { "type": "string" },
            "synopsis": { "type": "string" },
            "setup": { "type": "string" },
            "protagonistMotivation": { "type": "string" },
            "incitingEvent": { "type": "string" },
            "escalation": { "type": "string" },
            "complications": { "type": "array", "items": { "type": "string" } },
            "climaxChoice": { "type": "string" },
            "resolution": { "type": "string" },
            "emotionalArc": { "type": "string" },
            "canonAnchors": { "type": "array", "items": { "type": "string" } },
            "proposedElements": { "type": "array", "items": { "type": "string" } },
            "beatCount": { "type": "integer" }
          },
          "required": ["premise", "logline", "treatment", "synopsis", "setup", "protagonistMotivation", "incitingEvent", "escalation", "complications", "climaxChoice", "resolution", "emotionalArc", "canonAnchors", "proposedElements", "beatCount"]
        }
        """).RootElement.Clone(),
        "A duration-aware, canon-preserving synopsis proposal with explicit narrative structure.");
}

public sealed class DirectorSynopsisGenerationException(string message, Exception? innerException = null) : Exception(message, innerException);

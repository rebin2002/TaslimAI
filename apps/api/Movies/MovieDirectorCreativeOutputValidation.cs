using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class DirectorCreativeValidationReasonCodes
{
    public const string SchemaInvalid = "CREATIVE_SCHEMA_INVALID";
    public const string RequiredFieldMissing = "CREATIVE_REQUIRED_FIELD_MISSING";
    public const string EmptyOutput = "CREATIVE_OUTPUT_EMPTY";
    public const string ExcessiveLength = "CREATIVE_OUTPUT_TOO_LONG";
    public const string GenericTemplate = "CREATIVE_OUTPUT_GENERIC";
    public const string ContextUngrounded = "CREATIVE_OUTPUT_UNGROUNDED";
    public const string CharacterMismatch = "CREATIVE_CHARACTER_MISMATCH";
    public const string DuplicateContent = "CREATIVE_DUPLICATE_CONTENT";
    public const string UnsupportedLockedCanonClaim = "CREATIVE_LOCKED_CANON_UNSUPPORTED";
    public const string OutputLanguageMismatch = "CREATIVE_OUTPUT_LANGUAGE_MISMATCH";
    public const string BaseRevisionMismatch = "CREATIVE_BASE_REVISION_MISMATCH";
    public const string StructuredRepairApplied = "CREATIVE_STRUCTURED_REPAIR_APPLIED";
}

public sealed class MovieDirectorCreativeOutputValidationOptions
{
    public int MaxRepairAttempts { get; set; } = 1;
    public int MaxFindings { get; set; } = 16;
    public int MinimumGroundingAnchorLength { get; set; } = 4;
}

public sealed record DirectorCreativeValidationFinding(string ReasonCode, string Field);

public enum DirectorCreativeValidationOutcome
{
    Passed,
    Repaired,
    Rejected,
}

public sealed record DirectorCreativeOutputValidationResult(
    DirectorCreativeValidationOutcome Outcome,
    DirectorStoryActionPayload? Output,
    IReadOnlyList<DirectorCreativeValidationFinding> Findings,
    bool Retryable = false)
{
    public bool IsValid => Outcome is DirectorCreativeValidationOutcome.Passed or DirectorCreativeValidationOutcome.Repaired;
    public bool WasRepaired => Outcome == DirectorCreativeValidationOutcome.Repaired;
    public IReadOnlyList<string> ReasonCodes => Findings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class DirectorCreativeOutputValidationException : Exception
{
    public DirectorCreativeOutputValidationException(DirectorCreativeOutputValidationResult result)
        : base("The Director creative output did not pass quality validation.")
    {
        Result = result;
        ReasonCodes = result.ReasonCodes;
    }

    public DirectorCreativeOutputValidationResult Result { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
}

public interface IMovieDirectorCreativeOutputValidator
{
    DirectorCreativeOutputValidationResult ValidateAndRepair(DirectorStoryActionPayload output, DirectorStoryBoundedContextDto context);
    DirectorCreativeOutputValidationResult ValidateJson(string? serializedOutput, DirectorStoryBoundedContextDto context);
}

/// <summary>
/// Deterministic validation boundary for Director Story output. It owns no provider calls and
/// performs at most the configured number of local structured repairs, so system repair cannot
/// become an unbounded retry or a second billable generation.
/// </summary>
public sealed class MovieDirectorCreativeOutputValidator(
    IOptions<MovieDirectorCreativeOutputValidationOptions> options,
    ILogger<MovieDirectorCreativeOutputValidator> logger) : IMovieDirectorCreativeOutputValidator
{
    private readonly MovieDirectorCreativeOutputValidationOptions settings = options.Value;

    public DirectorCreativeOutputValidationResult ValidateJson(string? serializedOutput, DirectorStoryBoundedContextDto context)
    {
        if (string.IsNullOrWhiteSpace(serializedOutput))
            return Rejected([new(DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload")]);

        DirectorStoryActionPayload? output;
        try
        {
            output = JsonSerializer.Deserialize<DirectorStoryActionPayload>(serializedOutput, DirectorJson.Options);
        }
        catch (JsonException)
        {
            return Rejected([new(DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload")]);
        }

        return output is null
            ? Rejected([new(DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload")])
            : ValidateAndRepair(output, context);
    }

    public DirectorCreativeOutputValidationResult ValidateAndRepair(DirectorStoryActionPayload output, DirectorStoryBoundedContextDto context)
    {
        var candidate = output;
        var repairFindings = new List<DirectorCreativeValidationFinding>();
        var attempts = Math.Clamp(settings.MaxRepairAttempts, 0, 1);

        for (var attempt = 0; attempt <= attempts; attempt++)
        {
            var findings = Validate(candidate, context);
            if (findings.Count == 0)
            {
                if (repairFindings.Count == 0)
                    return new(DirectorCreativeValidationOutcome.Passed, candidate, []);

                logger.LogInformation(
                    "Movie Director creative output passed after bounded structured repair. Action={Action}; RepairCount={RepairCount}; RepairReasonCodes={RepairReasonCodes}",
                    SafeAction(candidate.Action), repairFindings.Count, string.Join(',', repairFindings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal)));
                return new(DirectorCreativeValidationOutcome.Repaired, candidate, repairFindings);
            }

            if (attempt >= attempts || !CanRepair(findings))
            {
                logger.LogWarning(
                    "Movie Director creative output rejected. Action={Action}; ReasonCodes={ReasonCodes}; FindingCount={FindingCount}",
                    SafeAction(candidate.Action), string.Join(',', findings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal)), findings.Count);
                return Rejected(findings);
            }

            var repaired = Repair(candidate, findings);
            if (repaired is null)
            {
                logger.LogWarning(
                    "Movie Director creative output could not be repaired. Action={Action}; ReasonCodes={ReasonCodes}",
                    SafeAction(candidate.Action), string.Join(',', findings.Select(item => item.ReasonCode).Distinct(StringComparer.Ordinal)));
                return Rejected(findings);
            }

            repairFindings.Add(new(DirectorCreativeValidationReasonCodes.StructuredRepairApplied, "payload"));
            candidate = repaired;
        }

        return Rejected([new(DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload")]);
    }

    private IReadOnlyList<DirectorCreativeValidationFinding> Validate(DirectorStoryActionPayload? output, DirectorStoryBoundedContextDto context)
    {
        var findings = new List<DirectorCreativeValidationFinding>();
        if (output is null)
            return [new(DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload")];

        AddIf(findings, string.IsNullOrWhiteSpace(output.Action) || !DirectorStoryActionTypes.All.Contains(output.Action), DirectorCreativeValidationReasonCodes.SchemaInvalid, "action");
        if (findings.Count > 0) return findings;

        try
        {
            var roundTrip = JsonSerializer.Deserialize<DirectorStoryActionPayload>(JsonSerializer.Serialize(output, DirectorJson.Options), DirectorJson.Options);
            AddIf(findings, roundTrip is null, DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload");
        }
        catch (JsonException)
        {
            Add(findings, DirectorCreativeValidationReasonCodes.SchemaInvalid, "payload");
        }

        var expectedRevision = context.CurrentRevision?.RevisionId ?? context.ApprovedRevision?.RevisionId;
        AddIf(findings, output.BaseRevisionId != expectedRevision, DirectorCreativeValidationReasonCodes.BaseRevisionMismatch, "baseRevisionId");

        var action = DirectorStoryActionTypes.Normalize(output.Action);
        switch (action)
        {
            case DirectorStoryActionTypes.DevelopPremise:
                ValidateText(findings, output.Premise, 8_000, "premise");
                RequireChange(findings, output, "premise");
                break;
            case DirectorStoryActionTypes.ImproveLogline:
                ValidateText(findings, output.Logline, 2_000, "logline");
                RequireChange(findings, output, "logline");
                break;
            case DirectorStoryActionTypes.ExpandSynopsis:
            case DirectorStoryActionTypes.TightenPacing:
                ValidateText(findings, output.Synopsis, 20_000, "synopsis");
                RequireChange(findings, output, "synopsis");
                break;
            case DirectorStoryActionTypes.CreateOrRefineTreatment:
                ValidateText(findings, output.Treatment, 40_000, "treatment");
                RequireChange(findings, output, "treatment");
                break;
            case DirectorStoryActionTypes.ProposeScreenplayScene:
                ValidateScene(findings, output.ProposedScene, context);
                RequireChange(findings, output, "screenplay_scene");
                break;
            case DirectorStoryActionTypes.RewriteSelectedPassage:
                ValidateTargetedReplacement(findings, output, context, "rewrite");
                RequireChange(findings, output, "screenplay_passage");
                break;
            case DirectorStoryActionTypes.ImproveDialogue:
                ValidateTargetedReplacement(findings, output, context, "dialogue");
                RequireChange(findings, output, "dialogue");
                break;
            case DirectorStoryActionTypes.IdentifyInconsistencies:
                AddIf(findings, output.Findings is null || output.Findings.Count == 0, DirectorCreativeValidationReasonCodes.RequiredFieldMissing, "findings");
                break;
        }

        ValidateDuplicateContent(findings, output);
        ValidateTextQuality(findings, output, context, action);
        return findings.Take(Math.Max(1, settings.MaxFindings)).ToArray();
    }

    private void ValidateTargetedReplacement(
        List<DirectorCreativeValidationFinding> findings,
        DirectorStoryActionPayload output,
        DirectorStoryBoundedContextDto context,
        string field)
    {
        AddIf(findings, !output.TargetSceneId.HasValue || !output.TargetElementId.HasValue, DirectorCreativeValidationReasonCodes.RequiredFieldMissing, field);
        ValidateText(findings, output.ReplacementContent, 20_000, "replacementContent");
        if (!output.TargetSceneId.HasValue || !output.TargetElementId.HasValue) return;

        var scene = AllScenes(context).FirstOrDefault(item => item.Id == output.TargetSceneId.Value);
        var element = scene?.Elements.FirstOrDefault(item => item.Id == output.TargetElementId.Value);
        AddIf(findings, scene is null || element is null, DirectorCreativeValidationReasonCodes.ContextUngrounded, field);
    }

    private void ValidateScene(List<DirectorCreativeValidationFinding> findings, MovieStorySceneRequest? scene, DirectorStoryBoundedContextDto context)
    {
        if (scene is null)
        {
            Add(findings, DirectorCreativeValidationReasonCodes.RequiredFieldMissing, "proposedScene");
            return;
        }

        ValidateText(findings, scene.SceneIdentifier, 80, "sceneIdentifier");
        ValidateText(findings, scene.Slugline, 500, "slugline");
        ValidateText(findings, scene.Synopsis, 20_000, "sceneSynopsis");
        AddIf(findings, scene.Elements is null || scene.Elements.Count == 0, DirectorCreativeValidationReasonCodes.RequiredFieldMissing, "elements");
        if (scene.Elements is null) return;

        var knownCharacters = KnownCharacters(context);
        var elementContents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in scene.Elements)
        {
            if (element is null)
            {
                Add(findings, DirectorCreativeValidationReasonCodes.SchemaInvalid, "elements");
                continue;
            }

            if (!MovieScreenplayElementTypes.Supported.Contains(element.ElementType))
                Add(findings, DirectorCreativeValidationReasonCodes.SchemaInvalid, "elementType");
            ValidateText(findings, element.Content, 20_000, "elementContent");
            AddIf(findings, !elementContents.Add(Normalize(element.Content)), DirectorCreativeValidationReasonCodes.DuplicateContent, "elementContent");
            if (element.ElementType.Equals(MovieScreenplayElementTypes.Dialogue, StringComparison.OrdinalIgnoreCase))
            {
                AddIf(findings, string.IsNullOrWhiteSpace(element.CharacterName), DirectorCreativeValidationReasonCodes.RequiredFieldMissing, "characterName");
                if (knownCharacters.Count > 0 && !string.IsNullOrWhiteSpace(element.CharacterName))
                    AddIf(findings, !knownCharacters.Contains(Normalize(element.CharacterName)), DirectorCreativeValidationReasonCodes.CharacterMismatch, "characterName");
            }
        }

        if (scene.MovieSceneId.HasValue)
            AddIf(findings, !context.RelevantMovieScenes.Any(item => item.Id == scene.MovieSceneId.Value), DirectorCreativeValidationReasonCodes.ContextUngrounded, "movieSceneId");
    }

    private void ValidateTextQuality(List<DirectorCreativeValidationFinding> findings, DirectorStoryActionPayload output, DirectorStoryBoundedContextDto context, string action)
    {
        if (action == DirectorStoryActionTypes.IdentifyInconsistencies) return;
        var text = CreativeText(output);
        AddIf(findings, string.IsNullOrWhiteSpace(text), DirectorCreativeValidationReasonCodes.EmptyOutput, "output");
        if (string.IsNullOrWhiteSpace(text)) return;

        var genericPatterns = new[]
        {
            "[insert ", "{{", "}}", "lorem ipsum", "placeholder text", "tbd", "todo:",
            "as an ai", "generated response", "your protagonist", "replace this text",
        };
        AddIf(findings, genericPatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)), DirectorCreativeValidationReasonCodes.GenericTemplate, "output");
        AddIf(findings, HasRepeatedSentence(text), DirectorCreativeValidationReasonCodes.DuplicateContent, "output");

        var anchors = ContextAnchors(context);
        if (anchors.Count > 0 && !anchors.Any(anchor => text.Contains(anchor, StringComparison.OrdinalIgnoreCase)))
            Add(findings, DirectorCreativeValidationReasonCodes.ContextUngrounded, "output");

        var canonClaim = new[] { "locked canon", "approved canon", "locked guide confirms", "according to the locked guide", "canon requires", "locked continuity says" };
        if (canonClaim.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            var lockedAnchors = LockedContextAnchors(context);
            AddIf(findings, lockedAnchors.Count == 0 || !lockedAnchors.Any(anchor => text.Contains(anchor, StringComparison.OrdinalIgnoreCase)), DirectorCreativeValidationReasonCodes.UnsupportedLockedCanonClaim, "output");
        }

        AddIf(findings, !MatchesLanguage(text, context.Language), DirectorCreativeValidationReasonCodes.OutputLanguageMismatch, "output");
    }

    private static void ValidateDuplicateContent(List<DirectorCreativeValidationFinding> findings, DirectorStoryActionPayload output)
    {
        var fields = new[]
        {
            (Name: "premise", Value: output.Premise),
            (Name: "logline", Value: output.Logline),
            (Name: "synopsis", Value: output.Synopsis),
            (Name: "treatment", Value: output.Treatment),
            (Name: "replacementContent", Value: output.ReplacementContent),
        }.Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToArray();
        var duplicate = fields.GroupBy(item => Normalize(item.Value), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        AddIf(findings, duplicate is not null, DirectorCreativeValidationReasonCodes.DuplicateContent, "storyFields");
    }

    private static bool CanRepair(IReadOnlyList<DirectorCreativeValidationFinding> findings) =>
        findings.Count > 0 && findings.All(item => item.ReasonCode is DirectorCreativeValidationReasonCodes.StructuredRepairApplied or DirectorCreativeValidationReasonCodes.DuplicateContent)
        && findings.Any(item => item.ReasonCode == DirectorCreativeValidationReasonCodes.DuplicateContent);

    private static DirectorStoryActionPayload? Repair(DirectorStoryActionPayload output, IReadOnlyList<DirectorCreativeValidationFinding> findings)
    {
        if (!findings.Any(item => item.ReasonCode == DirectorCreativeValidationReasonCodes.DuplicateContent)) return null;
        var scene = output.ProposedScene;
        if (scene is null || scene.Elements is null) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elements = scene.Elements.Where(item => item is not null && seen.Add(Normalize(item.Content))).ToList();
        if (elements.Count == scene.Elements.Count) return null;
        var repairedScene = new MovieStorySceneRequest
        {
            SceneIdentifier = scene.SceneIdentifier.Trim(),
            ActNumber = scene.ActNumber,
            SequenceNumber = scene.SequenceNumber,
            MovieSceneId = scene.MovieSceneId,
            Slugline = scene.Slugline.Trim(),
            Synopsis = scene.Synopsis?.Trim(),
            Elements = elements,
        };
        return output with { ProposedScene = repairedScene };
    }

    private static IReadOnlyList<DirectorScreenplaySceneContext> AllScenes(DirectorStoryBoundedContextDto context) =>
        (context.CurrentRevision?.Scenes ?? []).Concat(context.ApprovedRevision?.Scenes ?? []).GroupBy(item => item.Id).Select(item => item.First()).ToArray();

    private static IReadOnlySet<string> KnownCharacters(DirectorStoryBoundedContextDto context) =>
        context.RelevantCharacters.Select(item => Normalize(item.Name)).Where(item => item.Length > 0).Concat(
            AllScenes(context).SelectMany(item => item.Elements).Where(item => !string.IsNullOrWhiteSpace(item.CharacterName)).Select(item => Normalize(item.CharacterName))).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlySet<string> ContextAnchors(DirectorStoryBoundedContextDto context)
    {
        var values = new List<string>();
        values.AddRange(context.RelevantCharacters.Select(item => item.Name));
        values.AddRange(context.RelevantWorldReferences.Select(item => item.Name));
        values.AddRange(context.RelevantMovieScenes.SelectMany(item => new[] { item.Title, item.Summary }));
        values.AddRange(AllScenes(context).SelectMany(item => new[] { item.Slugline, item.Synopsis }.Where(value => value is not null).Select(value => value!)).Concat(AllScenes(context).SelectMany(item => item.Elements.Select(element => element.Content))));
        values.AddRange(new[] { context.CurrentRevision, context.ApprovedRevision }.Where(revision => revision is not null).SelectMany(revision => new[] { revision!.Premise, revision.Logline, revision.Synopsis, revision.Treatment }));
        values.AddRange(new[] { context.MovieBrief, context.Guide.VisualLanguage, context.Guide.CameraLanguage, context.Guide.ContinuityRules });
        var minimum = Math.Max(3, settings.MinimumGroundingAnchorLength);
        return values.SelectMany(value => ExtractAnchors(value, minimum)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlySet<string> LockedContextAnchors(DirectorStoryBoundedContextDto context)
    {
        var values = new List<string> { context.Guide.VisualLanguage, context.Guide.CameraLanguage, context.Guide.ContinuityRules };
        values.AddRange(context.RelevantCharacters.SelectMany(item => new[] { item.Name, item.ContinuityNotes }.Where(value => value is not null).Select(value => value!).Concat(item.LockedFacts?.Select(fact => fact.LockedValue) ?? [])));
        values.AddRange(context.RelevantWorldReferences.Select(item => item.VisualContinuityNotes).Where(value => value is not null).Select(value => value!));
        return values.SelectMany(value => ExtractAnchors(value, Math.Max(3, settings.MinimumGroundingAnchorLength))).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractAnchors(string? value, int minimumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var token in Regex.Split(value.Trim(), @"[^\p{L}\p{Nd}']+")
                     .Where(item => item.Length >= minimumLength && !StopWords.Contains(item)))
            yield return token;
    }

    private static string CreativeText(DirectorStoryActionPayload output)
    {
        var values = new List<string?> { output.Premise, output.Logline, output.Synopsis, output.Treatment, output.ReplacementContent, output.ProposedScene?.SceneIdentifier, output.ProposedScene?.Slugline, output.ProposedScene?.Synopsis };
        values.AddRange(output.ProposedScene?.Elements?.Select(item => item.Content) ?? []);
        values.AddRange(output.Findings ?? []);
        return string.Join(" ", values.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static bool HasRepeatedSentence(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?。！？])\s+")
            .Select(Normalize)
            .Where(item => item.Length >= 12)
            .ToArray();
        return sentences.GroupBy(item => item, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
    }

    private static bool MatchesLanguage(string text, string? language)
    {
        var expected = string.IsNullOrWhiteSpace(language) ? LanguageCodes.English : language.Trim().ToLowerInvariant();
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
    private static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static string SafeAction(string? action) => string.IsNullOrWhiteSpace(action) ? "unknown" : action.Trim()[..Math.Min(80, action.Trim().Length)];
    private static void ValidateText(List<DirectorCreativeValidationFinding> findings, string? value, int maximum, string field)
    {
        AddIf(findings, string.IsNullOrWhiteSpace(value), DirectorCreativeValidationReasonCodes.EmptyOutput, field);
        if (!string.IsNullOrWhiteSpace(value)) AddIf(findings, value.Trim().Length > maximum, DirectorCreativeValidationReasonCodes.ExcessiveLength, field);
    }
    private static void RequireChange(List<DirectorCreativeValidationFinding> findings, DirectorStoryActionPayload output, string field) => AddIf(findings, output.Changes is null || !output.Changes.Any(item => string.Equals(item.Field, field, StringComparison.OrdinalIgnoreCase)), DirectorCreativeValidationReasonCodes.RequiredFieldMissing, "changes");
    private static void AddIf(List<DirectorCreativeValidationFinding> findings, bool condition, string reasonCode, string field) { if (condition) Add(findings, reasonCode, field); }
    private static void Add(List<DirectorCreativeValidationFinding> findings, string reasonCode, string field) { if (!findings.Any(item => item.ReasonCode == reasonCode && item.Field == field)) findings.Add(new(reasonCode, field)); }
    private static DirectorCreativeOutputValidationResult Rejected(IReadOnlyList<DirectorCreativeValidationFinding> findings) => new(DirectorCreativeValidationOutcome.Rejected, null, findings, false);

    private static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "must", "before", "after", "while", "when", "where", "story", "scene", "protagonist", "central", "choice", "locked", "guide", "the", "و", "في", "من", "على", "هذا", "هذه", "أن", "الى",
    };
}

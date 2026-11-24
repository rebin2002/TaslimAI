using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieReferenceReadinessStatuses
{
    public const string Ready = "ready";
    public const string NeedsReview = "needs_review";
    public const string Blocked = "blocked";
    public const string Overridden = "overridden";
}

public static class MovieReferenceReadinessItemStates
{
    public const string Complete = "complete";
    public const string Missing = "missing";
    public const string Warning = "warning";
    public const string Blocked = "blocked";
}

public static class MovieReferenceReadinessOverrideSources
{
    public const string ShotExecution = "shot_execution";
    public const string ProductionRender = "production_render";
    public const string LegacyShotGeneration = "legacy_shot_generation";
}

public sealed record MovieReferenceReadinessItemDto(
    string Key,
    string Label,
    string State,
    bool IsRequired,
    bool BlocksGeneration,
    bool OverrideAllowed,
    string Detail,
    string? SourceType = null,
    Guid? SourceId = null);

public sealed record MovieReferenceReadinessOverrideDto(
    Guid Id,
    Guid RequestedByUserId,
    string Source,
    string Reason,
    IReadOnlyList<string> BypassedItemKeys,
    int ReadinessPercentage,
    string EvaluationHash,
    DateTime CreatedAtUtc);

public sealed record MovieReferenceReadinessDto(
    Guid MovieProjectId,
    Guid MovieSceneId,
    Guid MovieShotId,
    int ReadinessPercentage,
    string Status,
    bool ReadyForGeneration,
    bool OverrideAllowed,
    bool OverrideApplied,
    string EvaluationHash,
    DateTime EvaluatedAtUtc,
    IReadOnlyList<MovieReferenceReadinessItemDto> Items,
    IReadOnlyList<MovieReferenceReadinessItemDto> Missing,
    IReadOnlyList<MovieReferenceReadinessItemDto> Blocked,
    IReadOnlyList<MovieReferenceReadinessItemDto> Warnings,
    MovieReferenceReadinessOverrideDto? LatestOverride = null);

public static class MovieReferenceReadinessPolicy
{
    public static int CalculatePercentage(IReadOnlyCollection<MovieReferenceReadinessItemDto> items)
    {
        if (items.Count == 0) return 100;
        return (int)Math.Round(items.Count(item => item.State == MovieReferenceReadinessItemStates.Complete) * 100d / items.Count, MidpointRounding.AwayFromZero);
    }

    public static string DetermineStatus(IReadOnlyCollection<MovieReferenceReadinessItemDto> items)
    {
        if (items.Any(item => item.BlocksGeneration)) return MovieReferenceReadinessStatuses.Blocked;
        return items.Any(item => item.State is MovieReferenceReadinessItemStates.Warning or MovieReferenceReadinessItemStates.Missing)
            ? MovieReferenceReadinessStatuses.NeedsReview
            : MovieReferenceReadinessStatuses.Ready;
    }

    public static bool IsOverrideAllowed(IReadOnlyCollection<MovieReferenceReadinessItemDto> items) =>
        items.Any(item => item.BlocksGeneration) && items.Where(item => item.BlocksGeneration).All(item => item.OverrideAllowed);

    public static MovieReferenceReadinessItemDto EvaluatePropReference(
        string? shotText,
        IReadOnlyCollection<MovieProductionPropReferenceDto> props,
        bool important)
    {
        var raw = shotText ?? string.Empty;
        var enumerated = EnumeratedPropRequirements(raw);
        if (enumerated.Count > 0)
        {
            // An explicit Director prop list is a set of named requirements: every
            // enumerated prop must have a canonical World reference that covers it,
            // otherwise the item fails closed for the unmatched names.
            var unsatisfied = new List<string>();
            MovieProductionPropReferenceDto? representative = null;
            foreach (var requirement in enumerated)
            {
                var prop = props.FirstOrDefault(candidate => ContainsWord(requirement, candidate.Name) || ContainsWord(candidate.Name, requirement));
                if (prop is null) unsatisfied.Add(requirement);
                else representative ??= prop;
            }
            if (unsatisfied.Count > 0)
                return new("props", "Prop references", MovieReferenceReadinessItemStates.Missing, important, important, !important, $"The shot explicitly requires {string.Join(", ", unsatisfied)}, but no named canonical World prop reference matches every required prop.", "world_prop");
            return new("props", "Prop references", MovieReferenceReadinessItemStates.Complete, true, false, true, $"Canonical prop reference '{representative!.Name}' is attached to this shot.", "world_prop", representative.PropId);
        }

        var cleaned = SignalText(raw);
        var matched = props.FirstOrDefault(prop => ContainsWord(cleaned, prop.Name));
        // English terms match whole tokens; CJK/Hangul/Thai terms match as
        // contiguous character sequences (compact scripts); Arabic terms match
        // whole tokens after diacritic folding.
        var propMentioned = ContainsAny(cleaned,
            "prop", "props", "object", "notebook", "compass", "key", "seed", "weapon",
            "道具", "物品", "笔记本", "罗盘", "指南针", "钥匙", "种子", "武器",
            "مفتاح", "أداة", "سلاح");

        if (!propMentioned && matched is null)
            return new("props", "Prop references", MovieReferenceReadinessItemStates.Complete, false, false, true, "No prop reference is applicable to this shot.");
        if (matched is not null)
            return new("props", "Prop references", MovieReferenceReadinessItemStates.Complete, true, false, true, $"Canonical prop reference '{matched.Name}' is attached to this shot.", "world_prop", matched.PropId);
        return new("props", "Prop references", MovieReferenceReadinessItemStates.Missing, important, important, !important, "The shot calls for a prop, but no named canonical World prop reference matches the shot.", "world_prop");
    }

    private static readonly Regex EnumeratedPropsHeader = new(
        @"\bImportant\s+props:\s*(?<items>[^.\n]+)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<string> EnumeratedPropRequirements(string text)
    {
        var match = EnumeratedPropsHeader.Match(text);
        if (!match.Success) return [];
        return match.Groups["items"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // The Director shot-plan apply executor always emits an "Important props: ." header
    // even when the approved plan carries no canonical props, so the empty placeholder
    // must not count as a genuine prop requirement. Headers that list real props keep
    // the signal and remain effective.
    private static readonly Regex EmptyPropRequirementHeader = new(
        @"\bImportant\s+props:\s*\.(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string SignalText(string text) =>
        text.Length == 0 ? text : EmptyPropRequirementHeader.Replace(text, " ");

    // Prop names and requirement signals match whole tokens inside Unicode words only,
    // so unrelated substrings such as "key" inside "monkey" or "compass" inside
    // "compassion" cannot falsely satisfy a prop reference. A multi-word prop name
    // matches when its token sequence appears contiguously in the shot text.
    private static bool ContainsWord(string? text, string? value)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        // Scripts that do not separate words with spaces (Han, Kana, Hangul with
        // agglutinative particles, Thai, Lao, Khmer, Myanmar) must match by
        // contiguous character sequence, ignoring combining marks; a whole Chinese
        // sentence tokenizes as one "word", so token equality alone would miss
        // canonical props such as "指南针" inside "玛拉拿起指南针然后离开".
        // Space-delimited scripts (Latin, Cyrillic, Greek, Arabic, Hebrew, Indic)
        // keep strict token boundaries, preserving the key/monkey fix.
        if (IsCompactPhrase(value))
            return FoldText(text).Contains(FoldText(value), StringComparison.OrdinalIgnoreCase);
        var phrase = Tokenize(value);
        if (phrase.Count == 0) return false;
        var tokens = Tokenize(text);
        for (var start = 0; start + phrase.Count <= tokens.Count; start++)
        {
            var matches = true;
            for (var index = 0; index < phrase.Count; index++)
            {
                if (!string.Equals(tokens[start + index], phrase[index], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return true;
        }
        return false;
    }

    private static bool IsCompactPhrase(string value)
    {
        var sawCompact = false;
        foreach (var character in value)
        {
            if (IsCompactScriptCharacter(character)) { sawCompact = true; continue; }
            if (IsCombiningMark(character) || character is ' ' or '\'' or '\u2019' or '\u200C') continue;
            return false;
        }
        return sawCompact;
    }

    // CJK, Kana, Hangul, Thai, Lao, Khmer and Myanmar ranges: written without
    // inter-word spaces.
    private static bool IsCompactScriptCharacter(char value) =>
        value is >= '\u0E00' and <= '\u0E7F'   // Thai
            or >= '\u0E80' and <= '\u0EFF'     // Lao
            or >= '\u1000' and <= '\u109F'     // Myanmar
            or >= '\u1780' and <= '\u17FF'     // Khmer
            or >= '\u2E80' and <= '\u303F'     // CJK radicals, Kangxi, punctuation
            or >= '\u3040' and <= '\u30FF'     // Hiragana, Katakana
            or >= '\u3100' and <= '\u312F'     // Bopomofo
            or >= '\u3400' and <= '\u4DBF'     // Han extension A
            or >= '\u4E00' and <= '\u9FFF'     // Han
            or >= '\uAC00' and <= '\uD7A3'     // Hangul
            or >= '\uF900' and <= '\uFAFF';    // CJK compatibility

    private static string FoldText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!IsCombiningMark(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool ContainsAny(string? text, params string[] terms) => terms.Any(term => ContainsWord(text, term));

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var runStart = -1;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && IsWordCharacter(value[index]))
            {
                if (runStart < 0) runStart = index;
            }
            else if (runStart >= 0)
            {
                tokens.Add(FoldToken(value.Substring(runStart, index - runStart)));
                runStart = -1;
            }
        }
        return tokens;
    }

    // Intra-word Unicode marks (apostrophe, curly apostrophe, zero-width non-joiner)
    // stay inside a token so names like "Mara's notebook" match as written, and
    // combining marks attach to their base token and are then folded away, so an
    // accented decomposed mention ("cafe" + U+0301) still matches the plain or
    // identically marked canonical name. A full composed/decomposed equivalence
    // map is unavailable here because this toolchain's reference assemblies omit
    // System.Globalization normalization types.
    private static string FoldToken(string token)
    {
        var anyMark = false;
        foreach (var character in token)
        {
            if (IsCombiningMark(character)) { anyMark = true; break; }
        }
        if (!anyMark) return token;
        var builder = new StringBuilder(token.Length);
        foreach (var character in token)
        {
            if (!IsCombiningMark(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool IsCombiningMark(char value) =>
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(value)
            is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark;

    private static bool IsWordCharacter(char value) =>
        char.IsLetterOrDigit(value)
        || value is '\'' or '\u2019' or '\u200C'
        || IsCombiningMark(value);
}

public sealed class MovieReferenceReadinessOverrideRequest
{
    public bool Confirm { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class MovieReferenceReadinessException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Append-only evidence that a user deliberately bypassed a safe, overridable
/// readiness blocker. It stores bounded provider-neutral facts only.
/// </summary>
public sealed class MovieReferenceReadinessOverrideAudit
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MovieSceneId { get; set; }
    public Guid MovieShotId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string BypassedItemKeysJson { get; set; } = "[]";
    public int ReadinessPercentage { get; set; }
    public string EvaluationHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public interface IMovieReferenceReadinessService
{
    Task<MovieReferenceReadinessDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
    Task<MovieReferenceReadinessDto?> EvaluateAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
    Task<MovieReferenceReadinessOverrideDto?> RecordOverrideAsync(Guid userId, Guid shotId, MovieReferenceReadinessOverrideRequest request, string source, CancellationToken cancellationToken = default);
    Task EnsureCanGenerateAsync(Guid userId, Guid shotId, bool allowOverride, string? overrideReason, string source, CancellationToken cancellationToken = default);
}

public sealed class MovieReferenceReadinessService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieProductionReferencePackageService references) : IMovieReferenceReadinessService
{
    private const int MaxReasonLength = 2_000;
    private const int MaxBypassedKeys = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public Task<MovieReferenceReadinessDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default) =>
        EvaluateAsync(userId, shotId, cancellationToken);

    public async Task<MovieReferenceReadinessDto?> EvaluateAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default)
    {
        var shot = await LoadShotAsync(shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var package = await references.GetForShotAsync(userId, shotId, cancellationToken);
        return package is null ? null : await EvaluateCoreAsync(shot, package, cancellationToken);
    }

    public async Task<MovieReferenceReadinessOverrideDto?> RecordOverrideAsync(
        Guid userId,
        Guid shotId,
        MovieReferenceReadinessOverrideRequest request,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirm) throw new MovieReferenceReadinessException("MOVIE_REFERENCE_OVERRIDE_CONFIRMATION_REQUIRED", "Confirm the reference readiness override explicitly.");
        var evaluation = await EvaluateAsync(userId, shotId, cancellationToken);
        if (evaluation is null) return null;
        EnsureOverrideIsSafe(evaluation, request.Reason);
        return await SaveOverrideAsync(userId, evaluation, request.Reason, source, cancellationToken);
    }

    public async Task EnsureCanGenerateAsync(
        Guid userId,
        Guid shotId,
        bool allowOverride,
        string? overrideReason,
        string source,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(userId, shotId, cancellationToken);
        if (evaluation is null)
            throw new MovieReferenceReadinessException("MOVIE_REFERENCE_READINESS_UNAVAILABLE", "Reference readiness could not be assembled for this shot; generation is blocked until the references are available.");
        if (evaluation.ReadyForGeneration) return;
        if (!allowOverride)
            throw new MovieReferenceReadinessException("MOVIE_REFERENCE_READINESS_BLOCKED", BuildBlockedMessage(evaluation));
        EnsureOverrideIsSafe(evaluation, overrideReason);
        await SaveOverrideAsync(userId, evaluation, overrideReason!, source, cancellationToken);
    }

    private async Task<MovieReferenceReadinessDto> EvaluateCoreAsync(
        MovieShot shot,
        MovieProductionReferencePackageDto package,
        CancellationToken cancellationToken)
    {
        var nextShot = await db.MovieShots.AsNoTracking()
            .Where(item => item.MovieSceneId == shot.MovieSceneId && item.ArchivedAt == null && item.Sequence > shot.Sequence)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var subjectIds = MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson);
        var important = IsImportant(shot);
        var plan = CinematographyShotPlanValidator.FromJson(shot.CinematographyJson);
        var characterItems = EvaluateCharacters(shot, package, subjectIds, important);
        var wardrobeItems = EvaluateWardrobe(shot, package, subjectIds, important);
        var locationItem = EvaluateLocation(shot, package, important);
        var lightingItem = EvaluateLighting(shot, package, plan, important);
        var propsItem = EvaluateProps(shot, package, important);
        var spatialItem = EvaluateSpatialOrientation(shot, plan, important);
        var cameraItem = EvaluateCamera(shot, plan, important);
        var dialogueItem = EvaluateDialogue(shot);
        var continuityItem = EvaluateContinuity(shot, package, important);
        var transitionItem = EvaluateTransitionOut(shot, package, plan, nextShot, important);

        var items = characterItems
            .Concat(wardrobeItems)
            .Append(locationItem)
            .Append(lightingItem)
            .Append(propsItem)
            .Append(spatialItem)
            .Append(cameraItem)
            .Append(dialogueItem)
            .Append(continuityItem)
            .Append(transitionItem)
            .ToArray();
        var percentage = MovieReferenceReadinessPolicy.CalculatePercentage(items);
        var blocked = items.Where(item => item.BlocksGeneration).ToArray();
        var warnings = items.Where(item => item.State == MovieReferenceReadinessItemStates.Warning).ToArray();
        var missing = items.Where(item => item.State == MovieReferenceReadinessItemStates.Missing).ToArray();
        var evaluationHash = Hash(new
        {
            shot.Id,
            shot.UpdatedAt,
            package.PackageHash,
            nextShotId = nextShot?.Id,
            nextShotSequence = nextShot?.Sequence,
            items = items.Select(item => new { item.Key, item.State, item.BlocksGeneration, item.Detail }),
        });
        var latestOverride = await db.MovieReferenceReadinessOverrideAudits.AsNoTracking()
            .Where(item => item.MovieShotId == shot.Id)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var overrideApplied = latestOverride is not null && string.Equals(latestOverride.EvaluationHash, evaluationHash, StringComparison.Ordinal);
        var status = overrideApplied ? MovieReferenceReadinessStatuses.Overridden : MovieReferenceReadinessPolicy.DetermineStatus(items);
        return new MovieReferenceReadinessDto(
            shot.Scene.MovieProjectId,
            shot.MovieSceneId,
            shot.Id,
            percentage,
            status,
            blocked.Length == 0 || overrideApplied,
            MovieReferenceReadinessPolicy.IsOverrideAllowed(items),
            overrideApplied,
            evaluationHash,
            DateTime.UtcNow,
            items,
            missing,
            blocked,
            warnings,
            latestOverride is null ? null : ToOverrideDto(latestOverride));
    }

    private async Task<MovieShot?> LoadShotAsync(Guid shotId, CancellationToken cancellationToken) =>
        await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);

    private async Task<MovieReferenceReadinessOverrideDto> SaveOverrideAsync(
        Guid userId,
        MovieReferenceReadinessDto evaluation,
        string reason,
        string source,
        CancellationToken cancellationToken)
    {
        var audit = new MovieReferenceReadinessOverrideAudit
        {
            Id = Guid.NewGuid(),
            MovieProjectId = evaluation.MovieProjectId,
            MovieSceneId = evaluation.MovieSceneId,
            MovieShotId = evaluation.MovieShotId,
            RequestedByUserId = userId,
            Source = NormalizeSource(source),
            Reason = CleanReason(reason),
            BypassedItemKeysJson = JsonSerializer.Serialize(evaluation.Blocked.Select(item => item.Key).Take(MaxBypassedKeys).ToArray(), JsonOptions),
            ReadinessPercentage = evaluation.ReadinessPercentage,
            EvaluationHash = evaluation.EvaluationHash,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.MovieReferenceReadinessOverrideAudits.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        return ToOverrideDto(audit);
    }

    private static MovieReferenceReadinessOverrideDto ToOverrideDto(MovieReferenceReadinessOverrideAudit audit)
    {
        IReadOnlyList<string> keys;
        try { keys = JsonSerializer.Deserialize<string[]>(audit.BypassedItemKeysJson, JsonOptions) ?? []; }
        catch (JsonException) { keys = []; }
        return new(audit.Id, audit.RequestedByUserId, audit.Source, audit.Reason, keys, audit.ReadinessPercentage, audit.EvaluationHash, audit.CreatedAtUtc);
    }

    private static void EnsureOverrideIsSafe(MovieReferenceReadinessDto evaluation, string? reason)
    {
        if (!evaluation.OverrideAllowed)
            throw new MovieReferenceReadinessException("MOVIE_REFERENCE_OVERRIDE_NOT_ALLOWED", "This readiness block protects an important continuity reference and cannot be bypassed safely.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10 || reason.Trim().Length > MaxReasonLength)
            throw new MovieReferenceReadinessException("MOVIE_REFERENCE_OVERRIDE_REASON_INVALID", "Explain the safe production reason for overriding this readiness block in 10–2,000 characters.");
    }

    private static string BuildBlockedMessage(MovieReferenceReadinessDto evaluation) =>
        $"Shot reference readiness is blocked at {evaluation.ReadinessPercentage}%. Resolve: {string.Join(", ", evaluation.Blocked.Select(item => item.Label))}.";

    private static MovieReferenceReadinessItemDto[] EvaluateCharacters(
        MovieShot shot,
        MovieProductionReferencePackageDto package,
        IReadOnlyList<Guid> subjectIds,
        bool important)
    {
        var textCharacterIds = package.Characters
            .Where(item => ContainsWord(string.Join(" ", shot.Description, shot.Subjects, shot.Purpose), item.Name))
            .Select(item => item.CharacterId)
            .ToArray();
        var expected = subjectIds.Count > 0 ? subjectIds : textCharacterIds;
        if (expected.Count == 0)
            return [Complete("character", "Character reference", false, "No canonical character is declared for this shot.")];
        var missing = expected.Where(id => package.Characters.All(item => item.CharacterId != id)).ToArray();
        return missing.Length > 0
            ? [Item("character", "Character reference", MovieReferenceReadinessItemStates.Missing, true, true, false, "Every on-screen character must resolve to an existing Cast record before video generation.")]
            : [Complete("character", "Character reference", true, $"{expected.Count} canonical character reference{(expected.Count == 1 ? " is" : "s are")} available.", "character_continuity")];
    }

    private static MovieReferenceReadinessItemDto[] EvaluateWardrobe(
        MovieShot shot,
        MovieProductionReferencePackageDto package,
        IReadOnlyList<Guid> subjectIds,
        bool important)
    {
        var expected = subjectIds.Count > 0 ? subjectIds : package.Characters
            .Where(item => ContainsWord(string.Join(" ", shot.Description, shot.Subjects, shot.Purpose), item.Name))
            .Select(item => item.CharacterId)
            .ToArray();
        if (expected.Count == 0)
            return [Complete("wardrobe", "Wardrobe reference", false, "No character wardrobe is applicable to this shot.")];
        var missing = expected.Where(id => package.Wardrobe.All(item => item.CharacterId != id || string.IsNullOrWhiteSpace(item.Wardrobe))).ToArray();
        return missing.Length > 0
            ? [Item("wardrobe", "Wardrobe reference", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Add a character-state wardrobe or an explicit wardrobe lock for every on-screen character.", "character_wardrobe")]
            : [Complete("wardrobe", "Wardrobe reference", true, "Wardrobe is resolved from the applicable character state or continuity lock.", "character_wardrobe")];
    }

    private static MovieReferenceReadinessItemDto EvaluateLocation(MovieShot shot, MovieProductionReferencePackageDto package, bool important)
    {
        if (string.IsNullOrWhiteSpace(shot.LocationSet))
            return Item("location", "Location / set reference", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Identify the canonical location or set before generating this shot.");
        var location = package.Locations.FirstOrDefault(item => ReferenceNameMatches(shot.LocationSet, item.Name));
        var set = package.World.Sets.FirstOrDefault(item => ReferenceNameMatches(shot.LocationSet, item.Name));
        if (location is not null) return Complete("location", "Location / set reference", true, $"Canonical location '{location.Name}' is attached to this shot.", "world_location", location.LocationId);
        if (set is not null) return Complete("location", "Location / set reference", true, $"Canonical set '{set.Name}' is attached to this shot.", "world_set", set.SetId);
        return Item("location", "Location / set reference", MovieReferenceReadinessItemStates.Missing, important, important, !important, "The shot names a location or set, but no matching World usage is attached to this shot.");
    }

    private static MovieReferenceReadinessItemDto EvaluateLighting(MovieShot shot, MovieProductionReferencePackageDto package, CinematographyShotPlan? plan, bool important)
    {
        var lighting = plan?.LightingIntent ?? ReadJsonString(shot.CinematographyJson, "lightingIntent", "lighting", "colorAndLighting", "lightingReference");
        if (!string.IsNullOrWhiteSpace(lighting)) return Complete("lighting", "Lighting reference", true, $"Lighting intent '{lighting}' is present.", "cinematography");
        if (!string.IsNullOrWhiteSpace(package.Style.ColorAndLighting))
            return Item("lighting", "Lighting reference", MovieReferenceReadinessItemStates.Warning, important, important, !important, "A project lighting reference exists, but this shot has no shot-specific lighting intent.", "production_style");
        return Item("lighting", "Lighting reference", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Add a shot lighting intent or a grounded lighting reference.");
    }

    private static MovieReferenceReadinessItemDto EvaluateProps(MovieShot shot, MovieProductionReferencePackageDto package, bool important)
    {
        return MovieReferenceReadinessPolicy.EvaluatePropReference(
            string.Join(" ", shot.Description, shot.Subjects, shot.Purpose, shot.ProductionRequirements),
            package.Props,
            important);
    }

    private static MovieReferenceReadinessItemDto EvaluateSpatialOrientation(MovieShot shot, CinematographyShotPlan? plan, bool important)
    {
        var orientation = plan is not null
            ? $"{plan.CameraPosition} / {plan.CameraAngle} / {plan.CompositionIntent}"
            : ReadJsonString(shot.CinematographyJson, "cameraPosition", "cameraAngle", "compositionNotes", "spatialOrientation", "blocking", "screenDirection");
        if (!string.IsNullOrWhiteSpace(orientation) || ContainsAny(string.Join(" ", shot.CameraAndFraming, shot.VisualContinuityNotes, shot.ContinuityReferences), "left", "right", "front", "behind", "side", "profile", "over shoulder", "screen direction", "blocking", "foreground", "background"))
            return Complete("spatial_orientation", "Spatial orientation", true, "Subject/camera orientation is grounded in the shot plan.", "cinematography");
        return Item("spatial_orientation", "Spatial orientation", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Add blocking, screen direction, subject position, or a structured camera position to preserve spatial continuity.");
    }

    private static MovieReferenceReadinessItemDto EvaluateCamera(MovieShot shot, CinematographyShotPlan? plan, bool important)
    {
        if (plan is not null || !string.IsNullOrWhiteSpace(shot.CameraAndFraming) && !string.IsNullOrWhiteSpace(shot.CameraMotion))
            return Complete("camera_plan", "Camera plan", true, "Framing and camera movement are specified.", "cinematography");
        return Item("camera_plan", "Camera plan", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Add framing plus camera movement or a valid structured cinematography plan.");
    }

    private static MovieReferenceReadinessItemDto EvaluateDialogue(MovieShot shot)
    {
        if (string.IsNullOrWhiteSpace(shot.Dialogue))
            return Complete("dialogue", "Dialogue reference", false, "No dialogue is planned for this shot.");
        return Item("dialogue", "Dialogue reference", MovieReferenceReadinessItemStates.Warning, false, false, true, "Dialogue text is present; confirm speaker, timing, and approved voice coverage before final sync.", "shot_plan", shot.Id);
    }

    private static MovieReferenceReadinessItemDto EvaluateContinuity(MovieShot shot, MovieProductionReferencePackageDto package, bool important)
    {
        var hardConflict = package.Warnings.Any(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) || item.Severity.Equals("blocked", StringComparison.OrdinalIgnoreCase));
        if (hardConflict)
            return Item("continuity", "Continuity reference", MovieReferenceReadinessItemStates.Blocked, true, true, false, "The existing continuity package reports a blocking conflict; resolve the authoritative Cast, World, or Guide record first.", "continuity_engine");
        if (!string.IsNullOrWhiteSpace(shot.ContinuityReferences) || !string.IsNullOrWhiteSpace(shot.VisualContinuityNotes))
            return Complete("continuity", "Continuity reference", true, "The shot carries explicit continuity references.", "shot_plan");
        if (!string.IsNullOrWhiteSpace(package.Style.ContinuityRules))
            return Item("continuity", "Continuity reference", MovieReferenceReadinessItemStates.Warning, important, important, !important, "Project continuity rules exist, but this shot has no explicit continuity hand-off.", "production_style");
        return Item("continuity", "Continuity reference", MovieReferenceReadinessItemStates.Missing, important, important, !important, "Add a continuity reference to the Guide, prior shot, Cast state, World fact, or locked prop position.");
    }

    private static MovieReferenceReadinessItemDto EvaluateTransitionOut(MovieShot shot, MovieProductionReferencePackageDto package, CinematographyShotPlan? plan, MovieShot? nextShot, bool important)
    {
        var transition = plan?.VisualTransitionIntent ?? ReadJsonString(shot.CinematographyJson, "visualTransitionIntent", "transitionOut", "transition");
        var lastFrameNotes = ReadJsonString(package.Keyframe?.CompositionJson, "lastFrameNotes", "transitionOut", "transition");
        if (!string.IsNullOrWhiteSpace(transition) || !string.IsNullOrWhiteSpace(lastFrameNotes) || ContainsAny(string.Join(" ", shot.VisualContinuityNotes, shot.ContinuityReferences), "transition", "cut", "hold", "reveal", "exit", "out"))
            return Complete("transition_out", "Transition-out reference", true, "The shot has an explicit outgoing transition or last-frame hand-off.", "cinematography");
        var detail = nextShot is null
            ? "Add a last-frame or outgoing transition note so the final edit has a deliberate hand-off."
            : "Add the outgoing transition or last-frame hand-off to preserve continuity into the next shot.";
        return Item("transition_out", "Transition-out reference", MovieReferenceReadinessItemStates.Warning, false, false, true, detail);
    }

    private static MovieReferenceReadinessItemDto Complete(string key, string label, bool required, string detail, string? sourceType = null, Guid? sourceId = null) =>
        new(key, label, MovieReferenceReadinessItemStates.Complete, required, false, true, detail, sourceType, sourceId);

    private static MovieReferenceReadinessItemDto Item(string key, string label, string state, bool required, bool blocks, bool overrideAllowed, string detail, string? sourceType = null, Guid? sourceId = null) =>
        new(key, label, state, required, blocks, overrideAllowed, detail, sourceType, sourceId);

    private static bool IsImportant(MovieShot shot) =>
        string.Equals(shot.NarrativeImportance, MovieShotNarrativeImportance.Primary, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(shot.NarrativeImportance, MovieShotNarrativeImportance.Critical, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(shot.ContinuitySensitivity, MovieShotContinuitySensitivities.High, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(shot.ContinuitySensitivity, MovieShotContinuitySensitivities.Locked, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value) => string.Join(" ", value.Trim().TrimEnd('.', ',', ';', ':').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool ReferenceNameMatches(string requested, string canonical)
    {
        var normalizedCanonical = NormalizeName(canonical);
        return NormalizeName(requested) == normalizedCanonical || requested.Split(new[] { '/', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => NormalizeName(part) == normalizedCanonical);
    }

    private static bool ContainsWord(string? text, string? value) =>
        !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(value) && text.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string? text, params string[] terms) => terms.Any(term => ContainsWord(text, term));

    private static string? ReadJsonString(string? json, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var propertyName in propertyNames)
            {
                if (document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!.Trim();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string NormalizeSource(string source) =>
        string.IsNullOrWhiteSpace(source) ? MovieReferenceReadinessOverrideSources.ProductionRender : source.Trim().ToLowerInvariant()[..Math.Min(source.Trim().Length, 80)];

    private static string CleanReason(string reason) => reason.Trim()[..Math.Min(reason.Trim().Length, MaxReasonLength)];

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)))).ToLowerInvariant();
}

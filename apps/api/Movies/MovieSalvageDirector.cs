namespace Taslim.Api.Movies;

/// <summary>
/// Product-safe vocabulary for planning the least-expensive repair before a new render.
/// These actions describe editorial intent only; this planner never executes media work.
/// </summary>
public static class MovieSalvageDirectorContract
{
    public const string Version = "movie-salvage-director.v1";
    public const int DefaultMaxOptions = 8;
    public const int MaxOptions = 8;
    public const int MaxIssueDescriptionLength = 2_000;
}

public static class MovieSalvageIssueTypes
{
    public const string Generic = "generic";
    public const string Duration = "duration";
    public const string Composition = "composition";
    public const string Continuity = "continuity";
    public const string Audio = "audio";
    public const string Selection = "selection";
    public const string Technical = "technical";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Generic, Duration, Composition, Continuity, Audio, Selection, Technical,
    };
}

public static class MovieSalvageActionTypes
{
    public const string Trim = "trim";
    public const string Crop = "crop";
    public const string AlternateSelect = "alternate_select";
    public const string ReactionInsert = "reaction_insert";
    public const string Insert = "insert";
    public const string CoverAngle = "cover_angle";
    public const string SoundBridge = "sound_bridge";
    public const string Transition = "transition";
    public const string Regenerate = "regenerate";
}

public static class MovieSalvageOptionScopes
{
    public const string Edit = "edit";
    public const string Audio = "audio";
    public const string Timeline = "timeline";
    public const string NewFootage = "new_footage";
}

/// <summary>
/// Bounded, user/product supplied evidence about what can be salvaged from existing footage.
/// A true field means the corresponding edit has already been identified as feasible; the
/// planner does not inspect media, infer a safe crop, or pretend that evidence exists.
/// </summary>
public sealed class MovieSalvageEditContext
{
    public bool HasUsableRange { get; set; }
    public bool HasSafeCrop { get; set; }
    public bool HasContinuitySafeAlternate { get; set; }
    public bool HasContinuitySafeReaction { get; set; }
    public bool HasContinuitySafeInsert { get; set; }
    public bool HasContinuitySafeCoverAngle { get; set; }
    public bool HasSoundBridge { get; set; }
    public bool HasAdjacentTransition { get; set; }
    public bool HasUsableAudio { get; set; } = true;
    public bool HasCleanCutPoint { get; set; } = true;
    public bool ReferencesLocked { get; set; } = true;
    public bool SelectedTakeIsCanonical { get; set; } = true;
    public bool HasVisualContinuityRisk { get; set; }
    public int? CurrentDurationSeconds { get; set; }
    public int? ExpectedDurationSeconds { get; set; }
    public decimal? CurrentAspectRatio { get; set; }
    public decimal? TargetAspectRatio { get; set; }
}

public sealed class MovieSalvageDirectorRequest
{
    public string IssueType { get; set; } = MovieSalvageIssueTypes.Generic;
    public string? IssueDescription { get; set; }
    public IReadOnlyList<MovieProductionQcFinding> QcFindings { get; set; } = [];
    public MovieSalvageEditContext Context { get; set; } = new();
    public int MaxOptions { get; set; } = MovieSalvageDirectorContract.DefaultMaxOptions;
}

public sealed record MovieSalvageRepairOption(
    int Order,
    string Action,
    string Scope,
    string Label,
    bool Available,
    bool UsesExistingFootage,
    bool RequiresGeneration,
    bool RequiresReferenceLock,
    bool PreservesCanonicalTake,
    bool RequiresUserApproval,
    string Rationale,
    string Explainability,
    IReadOnlyList<string> Preconditions,
    string Risk);

public sealed record MovieSalvagePlan(
    string ContractVersion,
    string IssueType,
    string? IssueDescription,
    bool FullRegenerationDeferred,
    bool ReferenceLockRequired,
    string Summary,
    IReadOnlyList<MovieSalvageRepairOption> Options);

public interface IMovieSalvageDirector
{
    MovieSalvagePlan Plan(MovieSalvageDirectorRequest request);
}

/// <summary>
/// Deterministic salvage policy. It orders reversible edits and bounded missing-footage repairs
/// ahead of full regeneration. It has no provider, model, prompt, storage, accounting, or media
/// dependencies, so planning is safe to call while all external generation is disabled.
/// </summary>
public sealed class MovieSalvageDirector : IMovieSalvageDirector
{
    private static readonly IReadOnlyDictionary<string, string[]> PriorityByIssue =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [MovieSalvageIssueTypes.Duration] = [MovieSalvageActionTypes.Trim, MovieSalvageActionTypes.ReactionInsert, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Composition] = [MovieSalvageActionTypes.Crop, MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.CoverAngle, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Continuity] = [MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.ReactionInsert, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.CoverAngle, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Audio] = [MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.ReactionInsert, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Selection] = [MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.Trim, MovieSalvageActionTypes.Crop, MovieSalvageActionTypes.ReactionInsert, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.CoverAngle, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Technical] = [MovieSalvageActionTypes.Trim, MovieSalvageActionTypes.Crop, MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.CoverAngle, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Transition],
            [MovieSalvageIssueTypes.Generic] = [MovieSalvageActionTypes.Trim, MovieSalvageActionTypes.Crop, MovieSalvageActionTypes.AlternateSelect, MovieSalvageActionTypes.ReactionInsert, MovieSalvageActionTypes.Insert, MovieSalvageActionTypes.CoverAngle, MovieSalvageActionTypes.SoundBridge, MovieSalvageActionTypes.Transition],
        };

    public MovieSalvagePlan Plan(MovieSalvageDirectorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);

        var issueType = ResolveIssueType(request.IssueType, request.QcFindings);
        var issueDescription = NormalizeDescription(request.IssueDescription);
        var maxOptions = Math.Clamp(request.MaxOptions, 1, MovieSalvageDirectorContract.MaxOptions);
        var context = request.Context;
        var options = new List<MovieSalvageRepairOption>();

        foreach (var action in PriorityByIssue[issueType])
        {
            var option = BuildOption(action, context);
            if (option.Available)
            {
                options.Add(option with { Order = options.Count + 1 });
                if (options.Count >= maxOptions - 1) break;
            }
        }

        // Regeneration is deliberately last and is never silently converted into execution.
        if (options.Count < maxOptions)
        {
            options.Add(new MovieSalvageRepairOption(
                options.Count + 1,
                MovieSalvageActionTypes.Regenerate,
                MovieSalvageOptionScopes.NewFootage,
                "Regenerate the affected shot",
                true,
                false,
                true,
                true,
                true,
                true,
                "Use only after the bounded salvage options are exhausted or evidence shows the source cannot be repaired.",
                "This is the fallback because a new shot replaces more raw footage, continuity context, and review work than a local repair.",
                RegenerationPreconditions(context),
                context.HasVisualContinuityRisk ? "High: re-check locked character, location, prop, framing, and spatial continuity." : "High: the whole shot must return through normal approval and QC."));
        }

        var hasSalvage = options.Any(item => item.Action != MovieSalvageActionTypes.Regenerate);
        var summary = hasSalvage
            ? $"{options.Count - 1} bounded salvage option(s) are ordered before full regeneration for the {issueType} issue."
            : "No evidenced local repair is available; full regeneration remains a review-only fallback.";
        return new MovieSalvagePlan(
            MovieSalvageDirectorContract.Version,
            issueType,
            issueDescription,
            true,
            !context.ReferencesLocked,
            summary,
            options);
    }

    public MovieSalvagePlan Plan(MovieProductionQcDecision decision, MovieSalvageEditContext context)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Plan(new MovieSalvageDirectorRequest
        {
            IssueType = string.Empty,
            QcFindings = decision.Findings,
            Context = context,
        });
    }

    private static MovieSalvageRepairOption BuildOption(string action, MovieSalvageEditContext context) => action switch
    {
        MovieSalvageActionTypes.Trim => Option(
            action, MovieSalvageOptionScopes.Edit, "Trim to the usable range", context.HasUsableRange && context.HasCleanCutPoint,
            true, false, false, true, "Remove unusable leading or trailing frames while keeping the strongest contiguous material.",
            "A bounded trim changes the smallest possible portion of the raw footage and avoids a new shot.",
            ["A measured usable range", "A clean cut point"], "Low: verify duration and the cut does not remove required story coverage."),
        MovieSalvageActionTypes.Crop => Option(
            action, MovieSalvageOptionScopes.Edit, "Crop to the target composition", context.HasSafeCrop,
            true, false, false, true, "Reframe the existing image when the subject and locked spatial relationships remain inside the safe crop.",
            "Cropping is preferred to re-rendering when composition is the only failing dimension.",
            ["A measured safe crop", "The crop keeps locked characters, locations, and props in frame"], "Medium: cropping can reduce resolution or alter eyelines."),
        MovieSalvageActionTypes.AlternateSelect => Option(
            action, MovieSalvageOptionScopes.Edit, "Select a continuity-safe alternate", context.HasContinuitySafeAlternate,
            true, false, false, true, "Replace the current selection with an already-reviewed alternate that matches the locked references and spatial continuity.",
            "Selection is cheaper and safer than creating new footage when an acceptable alternate already exists.",
            ["A continuity-safe alternate take", "The alternate is eligible for canonical selection"], "Low: re-run selection/QC; do not select by latest-created order."),
        MovieSalvageActionTypes.ReactionInsert => Option(
            action, MovieSalvageOptionScopes.NewFootage, "Insert a reaction beat", context.HasContinuitySafeReaction,
            false, true, true, true, "Generate only the missing reaction beat and bridge it to the usable source range.",
            "A short reaction insert repairs story clarity without replacing the complete shot.",
            ["A bounded reaction beat", "Locked reference package", "Continuity-safe character and location context"], "Medium: preserve eyeline, screen direction, wardrobe, and light."),
        MovieSalvageActionTypes.Insert => Option(
            action, MovieSalvageOptionScopes.NewFootage, "Insert a missing story beat", context.HasContinuitySafeInsert,
            false, true, true, true, "Generate only the missing insert and retain the usable source footage around it.",
            "A targeted insert follows salvage-before-regenerate: repair the missing information, not the entire scene.",
            ["A specifically bounded missing beat", "Locked reference package", "Continuity-safe insert location"], "Medium: verify timing and prop/location continuity at both joins."),
        MovieSalvageActionTypes.CoverAngle => Option(
            action, MovieSalvageOptionScopes.NewFootage, "Add a continuity-safe cover angle", context.HasContinuitySafeCoverAngle,
            false, true, true, true, "Replace only the defective visual moment with a cover angle that maintains screen direction and the locked spatial map.",
            "A cover angle hides a local defect while keeping the rest of the raw footage and edit intact.",
            ["A bounded defective range", "Locked reference package", "Continuity-safe cover framing"], "Medium: check eyeline, axis, lighting, and prop hand positions."),
        MovieSalvageActionTypes.SoundBridge => Option(
            action, MovieSalvageOptionScopes.Audio, "Use a sound bridge", context.HasSoundBridge && context.HasUsableAudio,
            true, false, false, true, "Carry clean dialogue or ambience across the visual join while the edit hides a short defective range.",
            "Audio continuity can cover a visual gap without generating replacement picture.",
            ["Usable source audio", "An identified bridge range"], "Low: inspect dialogue intelligibility and avoid masking a required visual action."),
        MovieSalvageActionTypes.Transition => Option(
            action, MovieSalvageOptionScopes.Timeline, "Use a bounded transition", context.HasAdjacentTransition,
            true, false, false, true, "Apply a short transition at the canonical timeline boundary when a hard cut is the remaining issue.",
            "A transition is the final editorial cover before requesting new footage; it must remain an explicit timeline decision.",
            ["Adjacent canonical timeline clips", "A bounded transition duration"], "Medium: transitions cannot repair a missing story beat or hard continuity conflict."),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported salvage action."),
    };

    private static MovieSalvageRepairOption Option(
        string action,
        string scope,
        string label,
        bool available,
        bool usesExistingFootage,
        bool requiresGeneration,
        bool requiresReferenceLock,
        bool preservesCanonicalTake,
        string rationale,
        string explainability,
        IReadOnlyList<string> preconditions,
        string risk) => new(
            0, action, scope, label, available, usesExistingFootage, requiresGeneration, requiresReferenceLock,
            preservesCanonicalTake, true, rationale, explainability, preconditions, risk);

    private static IReadOnlyList<string> RegenerationPreconditions(MovieSalvageEditContext context) =>
        context.ReferencesLocked
            ? ["Explicit user approval", "Locked reference package", "Normal production approval, QC, accounting, and recovery gates"]
            : ["Explicit user approval", "Lock character, location, prop, and spatial references before any new generation", "Normal production approval, QC, accounting, and recovery gates"];

    private static string ResolveIssueType(string? requested, IReadOnlyList<MovieProductionQcFinding>? findings)
    {
        var normalized = requested?.Trim().ToLowerInvariant() ?? string.Empty;
        if (MovieSalvageIssueTypes.Supported.Contains(normalized) && normalized != MovieSalvageIssueTypes.Generic)
            return normalized;
        foreach (var finding in findings ?? [])
        {
            if (finding.ReasonCode is MovieProductionQcReasonCodes.DurationMissing or MovieProductionQcReasonCodes.DurationOutOfTolerance)
                return MovieSalvageIssueTypes.Duration;
            if (finding.ReasonCode == MovieProductionQcReasonCodes.AspectRatioMismatch)
                return MovieSalvageIssueTypes.Composition;
            if (finding.Category.Equals("continuity", StringComparison.OrdinalIgnoreCase)
                || finding.ReasonCode is MovieProductionQcReasonCodes.ContinuityEvidenceMissing or MovieProductionQcReasonCodes.ContinuityHashMismatch or MovieProductionQcReasonCodes.HardContinuityConflict or MovieProductionQcReasonCodes.ContinuityWarningsExceeded)
                return MovieSalvageIssueTypes.Continuity;
        }
        return MovieSalvageIssueTypes.Supported.Contains(normalized) ? normalized : MovieSalvageIssueTypes.Generic;
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(description.Trim().Length, MovieSalvageDirectorContract.MaxIssueDescriptionLength)];
}

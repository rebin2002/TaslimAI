namespace Taslim.Api.AcademicWorkflow;

/// <summary>
/// The bounded, resumable stage sequence for academic projects:
/// plan → discover → screen → extract → synthesize → chapter draft → verify.
/// </summary>
public enum AcademicStage
{
    Plan = 0,
    Discover = 1,
    Screen = 2,
    Extract = 3,
    Synthesize = 4,
    DraftChapter = 5,
    Verify = 6,
}

public enum AcademicStageState
{
    NotStarted = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Invalidated = 4,
}

public enum AcademicProjectState
{
    Running = 0,
    AwaitingApproval = 1,
    PausedBudget = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum AcademicResearchLevel
{
    Masters = 0,
    Doctoral = 1,
}

public enum AcademicMethod
{
    SystematicReview = 0,
    NarrativeReview = 1,
    Qualitative = 2,
    Quantitative = 3,
    MixedMethods = 4,
    CaseStudy = 5,
    ScopingReview = 6,
    Comparative = 7,
    DoctrinalLegal = 8,
    SecondaryDesktop = 9,
}

public enum AcademicCitationStyle
{
    Apa7 = 0,
    Harvard = 1,
    Chicago = 2,
    Ieee = 3,
    Mla9 = 4,
    Vancouver = 5,
}

/// <summary>
/// How much of a source the platform is actually licensed to read. This drives every
/// quotation and locator rule: metadata is never a quotation, and an abstract is only
/// quotable from the abstract text itself. The engine refuses outputs that pretend
/// otherwise, so a fabricated page number or quotation fails integrity validation.
/// </summary>
public enum AcademicAccessLevel
{
    MetadataOnly = 0,
    Abstract = 1,
    FullText = 2,
}

public enum AcademicIdentifierKind
{
    Doi = 0,
    Isbn = 1,
    ArXiv = 2,
    Pmid = 3,
    Url = 4,
}

public enum AcademicScreeningDecision
{
    Included = 0,
    Excluded = 1,
    Uncertain = 2,
}

/// <summary>
/// Human review checkpoints. Each kind is emitted after its producing stage completes
/// and must be decided before the workflow advances past the checkpoint.
/// </summary>
public enum AcademicApprovalKind
{
    Plan = 0,
    SourceLibrary = 1,
    Synthesis = 2,
    ChapterDraft = 3,
    FinalVerification = 4,
}

public enum AcademicApprovalDecision
{
    Approved = 0,
    RequestRevision = 1,
    Rejected = 2,
}

public enum AcademicIssueSeverity
{
    Blocking = 0,
    Warning = 1,
}

/// <summary>
/// Canonical stage order. The engine only ever advances forward through this list;
/// approval revisions can move the cursor backward by invalidating stages.
/// </summary>
public static class AcademicStageOrder
{
    public static readonly IReadOnlyList<AcademicStage> Stages =
    [
        AcademicStage.Plan,
        AcademicStage.Discover,
        AcademicStage.Screen,
        AcademicStage.Extract,
        AcademicStage.Synthesize,
        AcademicStage.DraftChapter,
        AcademicStage.Verify,
    ];

    public static int Rank(AcademicStage stage) => (int)stage;

    public static IEnumerable<AcademicStage> From(AcademicStage stage) =>
        Stages.Skip(Rank(stage));

    /// <summary>The approval checkpoint emitted after a stage completes, if any.</summary>
    public static AcademicApprovalKind? ApprovalAfter(AcademicStage stage) => stage switch
    {
        AcademicStage.Plan => AcademicApprovalKind.Plan,
        AcademicStage.Screen => AcademicApprovalKind.SourceLibrary,
        AcademicStage.Synthesize => AcademicApprovalKind.Synthesis,
        AcademicStage.DraftChapter => AcademicApprovalKind.ChapterDraft,
        AcademicStage.Verify => AcademicApprovalKind.FinalVerification,
        _ => null,
    };

    public static AcademicStage StageFor(AcademicApprovalKind kind) => kind switch
    {
        AcademicApprovalKind.Plan => AcademicStage.Plan,
        AcademicApprovalKind.SourceLibrary => AcademicStage.Screen,
        AcademicApprovalKind.Synthesis => AcademicStage.Synthesize,
        AcademicApprovalKind.ChapterDraft => AcademicStage.DraftChapter,
        AcademicApprovalKind.FinalVerification => AcademicStage.Verify,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// Immutable, validated intake for a new academic project. <see cref="RequestKey"/> makes
/// creation idempotent: resubmitting the same key resumes the existing project instead of
/// starting a duplicate (and therefore never double-charges the budget).
/// </summary>
public sealed record AcademicWorkflowRequest
{
    private static readonly HashSet<string> KnownSourceTypes = new(StringComparer.Ordinal)
    {
        "journal_article", "book", "book_chapter", "edited_volume", "conference_paper", "thesis",
        "report", "dataset", "working_paper", "standard_or_guideline", "legislation", "other",
    };

    public required Guid WorkspaceId { get; init; }
    public required string RequestKey { get; init; }
    public required string ResearchQuestion { get; init; }
    public required string Discipline { get; init; }
    public AcademicResearchLevel Level { get; init; } = AcademicResearchLevel.Masters;
    public AcademicMethod Method { get; init; } = AcademicMethod.SystematicReview;
    public IReadOnlyList<AcademicMethod> SelectedMethods { get; init; } = [];
    public AcademicCitationStyle CitationStyle { get; init; } = AcademicCitationStyle.Apa7;
    public string Language { get; init; } = "en";
    public int MaxDiscoverySources { get; init; } = 120;
    public int MaxEvidencePerSource { get; init; } = 6;
    public IReadOnlyList<string> TargetChapterHeadings { get; init; } = [];
    public string? WorkingTitle { get; init; }
    public string? GeographicScope { get; init; }
    public string? PopulationOrContext { get; init; }
    public string? TimePeriod { get; init; }
    public IReadOnlyList<string> SubQuestions { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public int? MinimumSources { get; init; }
    public int? MinimumPeerReviewed { get; init; }
    public IReadOnlyList<string> AllowedSourceTypes { get; init; } = [];
    public bool RequireFullText { get; init; }
    public string? ProgrammeRequirements { get; init; }
    public AcademicBudgetPolicy Budget { get; init; } = new();
    public IReadOnlyList<AcademicApprovalKind> RequiredApprovals { get; init; } =
    [
        AcademicApprovalKind.Plan,
        AcademicApprovalKind.SourceLibrary,
        AcademicApprovalKind.Synthesis,
        AcademicApprovalKind.FinalVerification,
    ];
    public int MaxStageAttempts { get; init; } = 3;

    /// <summary>
    /// Fail-closed intake validation. Values are clamped by the API lane, never here,
    /// because an out-of-range academic budget must abort the request, not silently shrink.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RequestKey))
            throw Invalid(nameof(RequestKey), "A stable request key is required for idempotent creation.");
        if (string.IsNullOrWhiteSpace(ResearchQuestion) || ResearchQuestion.Length > 8_000)
            throw Invalid(nameof(ResearchQuestion), "The research question must contain 1 to 8,000 characters.");
        if (string.IsNullOrWhiteSpace(Discipline) || Discipline.Length > 200)
            throw Invalid(nameof(Discipline), "The discipline must contain 1 to 200 characters.");
        if (!Enum.IsDefined(Level) || !Enum.IsDefined(Method) || !Enum.IsDefined(CitationStyle) || SelectedMethods.Any(method => !Enum.IsDefined(method)))
            throw Invalid(nameof(Method), "The level, method, or citation style is not supported.");
        if (MaxDiscoverySources is < 1 or > 400)
            throw Invalid(nameof(MaxDiscoverySources), "Discovery must be bounded between 1 and 400 sources.");
        if (MaxEvidencePerSource is < 1 or > 20)
            throw Invalid(nameof(MaxEvidencePerSource), "Evidence per source must be bounded between 1 and 20.");
        if (MaxStageAttempts is < 1 or > 10)
            throw Invalid(nameof(MaxStageAttempts), "Stage attempts must be bounded between 1 and 10.");
        if (SelectedMethods.Count > 10 || SelectedMethods.Distinct().Count() != SelectedMethods.Count)
            throw Invalid(nameof(SelectedMethods), "Selected methods must be distinct and within the supported catalogue.");
        if (TargetChapterHeadings.Count > 24)
            throw Invalid(nameof(TargetChapterHeadings), "A workflow may draft at most 24 chapters.");
        if (TargetChapterHeadings.Any(heading => string.IsNullOrWhiteSpace(heading) || heading.Length > 200))
            throw Invalid(nameof(TargetChapterHeadings), "Chapter headings must contain 1 to 200 characters.");
        if (WorkingTitle is { Length: > 200 })
            throw Invalid(nameof(WorkingTitle), "The working title cannot exceed 200 characters.");
        if (GeographicScope is { Length: > 160 })
            throw Invalid(nameof(GeographicScope), "The geographic scope cannot exceed 160 characters.");
        if (PopulationOrContext is { Length: > 240 })
            throw Invalid(nameof(PopulationOrContext), "The population or context cannot exceed 240 characters.");
        if (TimePeriod is { Length: > 120 })
            throw Invalid(nameof(TimePeriod), "The time period cannot exceed 120 characters.");
        if (SubQuestions.Count > 4 || SubQuestions.Any(question => string.IsNullOrWhiteSpace(question) || question.Length > 300))
            throw Invalid(nameof(SubQuestions), "A project may contain up to 4 sub-questions of 1 to 300 characters each.");
        if (Keywords.Count > 12 || Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Length > 120))
            throw Invalid(nameof(Keywords), "A project may contain up to 12 keywords of 1 to 120 characters each.");
        if (MinimumSources is < 0 or > 1_000)
            throw Invalid(nameof(MinimumSources), "The minimum source count must be between 0 and 1,000.");
        if (MinimumPeerReviewed is < 0 or > 1_000 ||
            MinimumSources is int minimum && MinimumPeerReviewed is int peerMinimum && peerMinimum > minimum)
            throw Invalid(nameof(MinimumPeerReviewed), "The peer-reviewed minimum must be between 0 and the total source minimum.");
        if (AllowedSourceTypes.Count > 20 || AllowedSourceTypes.Distinct(StringComparer.Ordinal).Count() != AllowedSourceTypes.Count ||
            AllowedSourceTypes.Any(type => string.IsNullOrWhiteSpace(type) || !KnownSourceTypes.Contains(type)))
            throw Invalid(nameof(AllowedSourceTypes), "The allowed source-type list is invalid or too large.");
        if (ProgrammeRequirements is { Length: > 2_000 })
            throw Invalid(nameof(ProgrammeRequirements), "Programme requirements cannot exceed 2,000 characters.");
        if (RequiredApprovals.Distinct().Count() != RequiredApprovals.Count)
            throw Invalid(nameof(RequiredApprovals), "Approval checkpoints must be distinct.");
        if (!RequiredApprovals.Contains(AcademicApprovalKind.Plan))
            throw Invalid(nameof(RequiredApprovals), "The Plan approval checkpoint is required.");
        Budget.Validate();
    }

    private static AcademicWorkflowRequestException Invalid(string field, string reason) =>
        new("ACADEMIC_REQUEST_INVALID", $"{field}: {reason}");
}

/// <summary>
/// Static, safe limits for one project. The engine reserves the estimate before a stage
/// runs and commits measured usage afterwards; it can never exceed these ceilings and it
/// never re-spends an attempt it has already metered.
/// </summary>
public sealed record AcademicBudgetPolicy
{
    public decimal MaxTotalUsd { get; init; } = 12m;
    public decimal MaxStageUsd { get; init; } = 4m;
    public int MaxTotalProviderCalls { get; init; } = 400;
    public int MaxStageProviderCalls { get; init; } = 80;

    /// <summary>
    /// The estimate a stage may report is allowed to overshoot by this factor before the
    /// preflight refuses to start it. Overshoot beyond the ceiling pauses the project.
    /// </summary>
    public decimal StageEstimateSafetyFactor { get; init; } = 1.25m;

    public void Validate()
    {
        if (MaxTotalUsd <= 0m || MaxTotalUsd > 10_000m)
            throw new AcademicWorkflowRequestException("ACADEMIC_REQUEST_INVALID", "Total budget must be between 0 and 10,000 USD.");
        if (MaxStageUsd <= 0m || MaxStageUsd > MaxTotalUsd)
            throw new AcademicWorkflowRequestException("ACADEMIC_REQUEST_INVALID", "Stage budget must be positive and no larger than the total budget.");
        if (MaxTotalProviderCalls is < 1 or > 10_000)
            throw new AcademicWorkflowRequestException("ACADEMIC_REQUEST_INVALID", "Total provider calls must be between 1 and 10,000.");
        if (MaxStageProviderCalls < 1 || MaxStageProviderCalls > MaxTotalProviderCalls)
            throw new AcademicWorkflowRequestException("ACADEMIC_REQUEST_INVALID", "Stage provider calls must be positive and no larger than the total.");
        if (StageEstimateSafetyFactor < 1m || StageEstimateSafetyFactor > 2m)
            throw new AcademicWorkflowRequestException("ACADEMIC_REQUEST_INVALID", "The stage estimate safety factor must be between 1.0 and 2.0.");
    }
}

/// <summary>Cost/usage ceiling a stage handler believes its next attempt needs.</summary>
public sealed record AcademicStageEstimate
{
    public required decimal EstimatedCostUsd { get; init; }
    public required int EstimatedProviderCalls { get; init; }

    public static AcademicStageEstimate None => new() { EstimatedCostUsd = 0m, EstimatedProviderCalls = 0 };

    public void Validate()
    {
        if (EstimatedCostUsd < 0m)
            throw new InvalidOperationException("A stage estimate cannot be negative.");
        if (EstimatedProviderCalls < 0)
            throw new InvalidOperationException("A stage estimate cannot report negative provider calls.");
    }
}

/// <summary>Measured usage returned by a stage attempt, metered into the budget ledger.</summary>
public sealed record AcademicUsageRecord
{
    public required decimal CostUsd { get; init; }
    public required int ProviderCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    public static AcademicUsageRecord None => new() { CostUsd = 0m, ProviderCalls = 0 };

    public void Validate()
    {
        if (CostUsd < 0m || ProviderCalls < 0 || InputTokens < 0 || OutputTokens < 0)
            throw new InvalidOperationException("Measured usage cannot contain negative components.");
    }
}

/// <summary>The research plan produced by the Plan stage and reviewed by the student.</summary>
public sealed record AcademicResearchPlan
{
    public required int PlanVersion { get; init; }
    public required string ResearchQuestion { get; init; }
    public AcademicResearchLevel Level { get; init; } = AcademicResearchLevel.Masters;
    public AcademicMethod Method { get; init; } = AcademicMethod.SystematicReview;
    public IReadOnlyList<AcademicMethod> Methodologies { get; init; } = [];
    public AcademicCitationStyle CitationStyle { get; init; } = AcademicCitationStyle.Apa7;
    public required IReadOnlyList<string> Objectives { get; init; }
    public required IReadOnlyList<string> InclusionCriteria { get; init; }
    public required IReadOnlyList<string> ExclusionCriteria { get; init; }
    public required IReadOnlyList<AcademicSearchSpecification> SearchStrategy { get; init; }
    public required IReadOnlyList<string> ChapterOutline { get; init; }
    public string? WorkingTitle { get; init; }
    public string? GeographicScope { get; init; }
    public string? PopulationOrContext { get; init; }
    public string? TimePeriod { get; init; }
    public IReadOnlyList<string> SubQuestions { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public int? MinimumSources { get; init; }
    public int? MinimumPeerReviewed { get; init; }
    public IReadOnlyList<string> AllowedSourceTypes { get; init; } = [];
    public bool RequireFullText { get; init; }
    public string? ProgrammeRequirements { get; init; }
}

/// <summary>
/// One bounded query against an allow-listed scholarly catalog. Catalog kinds are a fixed
/// enum so no lane can smuggle an arbitrary endpoint into the discovery stage.
/// </summary>
public sealed record AcademicSearchSpecification
{
    public required AcademicCatalog Catalog { get; init; }
    public required string Query { get; init; }
    public IReadOnlyList<string> Filters { get; init; } = [];
}

public enum AcademicCatalog
{
    OpenAlex = 0,
    Crossref = 1,
    OpenLibrary = 2,
    GoogleBooks = 3,
    PubMed = 4,
    EuropePMC = 5,
    InstitutionalRepository = 6,
}

/// <summary>
/// A discovered scholarly work. Titles, authors and identifiers come from a catalog
/// response; the engine itself never mints source records. <see cref="DedupKey"/> is
/// the normalization identity used to collapse the same work across catalogs.
/// </summary>
public sealed record AcademicSourceRecord
{
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Authors { get; init; }
    public required string SourceType { get; init; }
    public required AcademicAccessLevel AccessLevel { get; init; }
    public required string DedupKey { get; init; }
    public AcademicIdentifierKind? IdentifierKind { get; init; }
    public string? IdentifierValue { get; init; }
    public string? Publisher { get; init; }
    public int? PublicationYear { get; init; }
    public string? Url { get; init; }

    /// <summary>Present only when the catalog actually supplied abstract text.</summary>
    public string? AbstractText { get; init; }

    /// <summary>Retraction notices must be surfaced and never silently cited as evidence.</summary>
    public bool IsRetracted { get; init; }

    /// <summary>Free-text note about the licensed access, e.g. "publisher terms: metadata only".</summary>
    public string? AccessNote { get; init; }
}

/// <summary>Screening verdict for one source, produced by the Screen stage.</summary>
public sealed record AcademicScreeningDecisionRecord
{
    public required string SourceId { get; init; }
    public required AcademicScreeningDecision Decision { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// One extracted finding. <see cref="Verbatim"/> means "quoted words of the author" and is
/// checked against the source's access level by the integrity guard; a paraphrase may cite
/// lower-access sources but still needs the source to be included at screening.
/// </summary>
public sealed record AcademicEvidenceItem
{
    public required string EvidenceId { get; init; }
    public required string SourceId { get; init; }
    public required string Topic { get; init; }

    /// <summary>Short text quoted or paraphrased from the source. Never invented content.</summary>
    public required string Content { get; init; }

    public bool Verbatim { get; init; }

    /// <summary>Page, section or paragraph locator. Only full-text sources may carry one.</summary>
    public string? Locator { get; init; }
}

/// <summary>A cross-source theme with explicit support and contradiction, from Synthesize.</summary>
public sealed record AcademicTheme
{
    public required string ThemeId { get; init; }
    public required string Statement { get; init; }
    public IReadOnlyList<string> SupportingEvidenceIds { get; init; } = [];
    public IReadOnlyList<string> ContradictingEvidenceIds { get; init; } = [];
    public IReadOnlyList<string> ResearchGaps { get; init; } = [];
}

public sealed record AcademicInlineQuote
{
    public required string SourceId { get; init; }
    public required string QuotedText { get; init; }
    public string? Locator { get; init; }
}

/// <summary>A citable block of chapter prose. Citations reference included source ids.</summary>
public sealed record AcademicChapterBlock
{
    public required string BlockId { get; init; }
    public required AcademicBlockKind Kind { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<string> CitationSourceIds { get; init; } = [];
    public IReadOnlyList<AcademicInlineQuote> Quotes { get; init; } = [];
}

public enum AcademicBlockKind
{
    Heading = 0,
    Paragraph = 1,
    Bullets = 2,
}

public sealed record AcademicChapterDraft
{
    public required string ChapterId { get; init; }
    public required string Heading { get; init; }
    public required IReadOnlyList<AcademicChapterBlock> Blocks { get; init; }
}

/// <summary>An integrity finding: Blocking findings prevent verification from passing.</summary>
public sealed record AcademicIntegrityIssue
{
    public required string Code { get; init; }
    public required string Detail { get; init; }
    public required AcademicIssueSeverity Severity { get; init; }
    public AcademicStage Stage { get; init; }
    public string? SourceId { get; init; }
    public string? ArtifactId { get; init; }
}

public sealed record AcademicVerificationReport
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<string> ChecksRun { get; init; }
    public IReadOnlyList<AcademicIntegrityIssue> Issues { get; init; } = [];
}

/// <summary>
/// The typed payload a stage handler returns. The engine enforces that a stage only sets
/// the fields it owns, which keeps provider output from corrupting other stages' artifacts.
/// </summary>
public sealed record AcademicStageOutput
{
    public AcademicResearchPlan? Plan { get; init; }
    public IReadOnlyList<AcademicSourceRecord>? Sources { get; init; }
    public IReadOnlyList<AcademicScreeningDecisionRecord>? Screening { get; init; }
    public IReadOnlyList<AcademicEvidenceItem>? Evidence { get; init; }
    public IReadOnlyList<AcademicTheme>? Themes { get; init; }
    public IReadOnlyList<AcademicChapterDraft>? Chapters { get; init; }
    public AcademicVerificationReport? Verification { get; init; }

    public bool IsEmpty =>
        Plan is null && Sources is null && Screening is null && Evidence is null &&
        Themes is null && Chapters is null && Verification is null;
}

public sealed record AcademicStageOutcome
{
    public required bool Succeeded { get; init; }
    public AcademicStageOutput? Output { get; init; }
    public AcademicUsageRecord Usage { get; init; } = AcademicUsageRecord.None;

    /// <summary>Retryable failures keep the project resumable; terminal failures fail it.</summary>
    public bool Retryable { get; init; }

    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }

    public static AcademicStageOutcome Completed(AcademicStageOutput output, AcademicUsageRecord? usage = null) =>
        new() { Succeeded = true, Output = output, Usage = usage ?? AcademicUsageRecord.None };

    public static AcademicStageOutcome Failure(string code, string detail, bool retryable, AcademicUsageRecord? usage = null) =>
        new() { Succeeded = false, ErrorCode = code, ErrorDetail = detail, Retryable = retryable, Usage = usage ?? AcademicUsageRecord.None };
}

public sealed class AcademicWorkflowRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

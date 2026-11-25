using Taslim.Api.AcademicWorkflow;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic in-memory implementation of the durable-state port with the same
/// replay semantics the integration store must provide: monotonic revisions, unique
/// checkpoint keys, and creation idempotency by request key.
/// </summary>
internal sealed class InMemoryAcademicWorkflowStateStore : IAcademicWorkflowStateStore
{
    private readonly Dictionary<Guid, AcademicWorkflowSnapshot> projects = [];
    private readonly HashSet<string> checkpointKeys = [];
    private readonly Dictionary<(Guid WorkspaceId, string RequestKey), Guid> byRequest = [];

    private readonly List<string> commitLog = [];
    public IReadOnlyList<string> CommitLog => commitLog;

    public Task<AcademicWorkflowSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(projects.TryGetValue(projectId, out var snapshot) ? snapshot.Clone() : null);

    public Task<AcademicWorkflowSnapshot?> FindByRequestKeyAsync(Guid workspaceId, string requestKey, CancellationToken cancellationToken) =>
        Task.FromResult(byRequest.TryGetValue((workspaceId, requestKey), out var id) ? projects[id].Clone() : null);

    public Task<bool> TryCreateAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = (snapshot.WorkspaceId, snapshot.RequestKey);
        if (byRequest.ContainsKey(key) || projects.ContainsKey(snapshot.ProjectId))
            return Task.FromResult(false);
        byRequest[key] = snapshot.ProjectId;
        projects[snapshot.ProjectId] = snapshot.Clone();
        return Task.FromResult(true);
    }

    public Task<AcademicCommitOutcome> TryCommitAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        CancellationToken cancellationToken)
    {
        commitLog.Add(checkpointKey);
        AcademicCommitStatus status;
        if (!projects.TryGetValue(snapshot.ProjectId, out var stored))
        {
            status = AcademicCommitStatus.RevisionConflict;
        }
        else if (checkpointKeys.Contains(checkpointKey))
        {
            status = AcademicCommitStatus.DuplicateCheckpoint;
        }
        else if (stored.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1)
        {
            status = AcademicCommitStatus.RevisionConflict;
        }
        else
        {
            checkpointKeys.Add(checkpointKey);
            projects[snapshot.ProjectId] = snapshot.Clone();
            status = AcademicCommitStatus.Committed;
        }

        return Task.FromResult(new AcademicCommitOutcome(status));
    }
}

/// <summary>Time provider whose clock advances one second per read: ordering, never drift.</summary>
internal sealed class SteadyTimeProvider : TimeProvider
{
    private DateTime current = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow()
    {
        var observed = current;
        current = current.AddSeconds(1);
        return new DateTimeOffset(observed, TimeSpan.Zero);
    }
}

/// <summary>
/// Scripted stage double: queued outcomes/exceptions, default snapshot-aware fixture
/// responses, and full call history so tests can assert on replay counts and contexts.
/// </summary>
internal sealed class ScriptedStageHandler(AcademicStage stage) : IAcademicStageHandler
{
    public AcademicStage Stage { get; } = stage;
    public List<AcademicStageContext> Calls { get; } = [];
    public Queue<AcademicStageOutcome> Script { get; } = new();
    public Queue<Exception> Exceptions { get; } = new();
    public Action<AcademicStageContext>? OnCall { get; set; }
    public AcademicStageEstimate EstimateValue { get; set; } = new() { EstimatedCostUsd = 0.5m, EstimatedProviderCalls = 2 };
    public AcademicUsageRecord UsagePerCall { get; set; } = new() { CostUsd = 0.5m, ProviderCalls = 2 };

    public AcademicStageEstimate Estimate(AcademicStageContext context) => EstimateValue;

    public Task<AcademicStageOutcome> ExecuteAsync(AcademicStageContext context, CancellationToken cancellationToken)
    {
        Calls.Add(context);
        OnCall?.Invoke(context);
        if (Exceptions.Count > 0)
            throw Exceptions.Dequeue();
        if (Script.Count > 0)
            return Task.FromResult(Script.Dequeue());
        return Task.FromResult(AcademicWorkflowFixtures.OutcomeFor(Stage, context.Snapshot, UsagePerCall));
    }

    public void QueueFailure(string code = "PROVIDER_UNAVAILABLE", string detail = "Transient provider failure.", bool retryable = true, AcademicUsageRecord? usage = null) =>
        Script.Enqueue(AcademicStageOutcome.Failure(code, detail, retryable, usage));

    public void QueueOutput(AcademicStageOutput output, AcademicUsageRecord? usage = null) =>
        Script.Enqueue(AcademicStageOutcome.Completed(output, usage));

    public void QueueException(Exception exception) => Exceptions.Enqueue(exception);
}

internal sealed class AcademicWorkflowHarness
{
    public InMemoryAcademicWorkflowStateStore Store { get; } = new();
    public Dictionary<AcademicStage, ScriptedStageHandler> Handlers { get; } = new();
    public AcademicWorkflowOrchestrator Orchestrator { get; }

    public AcademicWorkflowHarness(
        AcademicWorkflowRequest? requestOverride = null,
        AcademicWorkflowEngineOptions? options = null)
    {
        foreach (var stage in AcademicStageOrder.Stages)
            Handlers[stage] = new ScriptedStageHandler(stage);
        Orchestrator = new AcademicWorkflowOrchestrator(
            Store,
            Handlers.Values.Cast<IAcademicStageHandler>(),
            new SteadyTimeProvider(),
            options);
        Request = requestOverride ?? AcademicWorkflowFixtures.Request("harness-" + Guid.Empty.ToString("N"));
    }

    public AcademicWorkflowRequest Request { get; }

    private Guid? startedProjectId;

    public Guid ProjectId => startedProjectId
        ?? throw new InvalidOperationException("The harness project has not been started yet.");

    public async Task<AcademicWorkflowRunResult> StartAsync(string requestKey = "default-request")
    {
        var result = await Orchestrator.StartAsync(Request with { RequestKey = requestKey }, CancellationToken.None);
        startedProjectId = result.Snapshot?.ProjectId
            ?? AcademicWorkflowOrchestrator.DeriveProjectId(Request.WorkspaceId, requestKey);
        return result;
    }

    /// <summary>Drives the loop, approving every checkpoint, until a terminal or paused state.</summary>
    public async Task<AcademicWorkflowRunResult> DrainWithApprovalsAsync(string requestKey = "drain-request")
    {
        var result = await StartAsync(requestKey);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            switch (result.Status)
            {
                case AcademicWorkflowRunStatus.Completed:
                case AcademicWorkflowRunStatus.PausedBudget:
                case AcademicWorkflowRunStatus.Cancelled:
                case AcademicWorkflowRunStatus.ProjectFailed:
                    return result;
                case AcademicWorkflowRunStatus.AwaitingApproval:
                    await ApprovePendingAsync();
                    result = await Orchestrator.ResumeAsync(ProjectId, CancellationToken.None);
                    break;
                case AcademicWorkflowRunStatus.StageFailure:
                case AcademicWorkflowRunStatus.RevisionConflict:
                    result = await Orchestrator.ResumeAsync(ProjectId, CancellationToken.None);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected run status: " + result.Status);
            }
        }

        throw new InvalidOperationException("Drain did not reach a terminal state: " + result.Status);
    }

    /// <summary>Resumes, auto-approving earlier gates, until the given approval is pending.</summary>
    public async Task<AcademicWorkflowRunResult> ResumeUntilGateAsync(AcademicApprovalKind kind)
    {
        if (startedProjectId is null)
            await StartAsync("until-gate-" + kind);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var result = await Orchestrator.ResumeAsync(ProjectId, CancellationToken.None);
            if (result.Status == AcademicWorkflowRunStatus.AwaitingApproval)
            {
                if (result.Snapshot!.PendingApproval!.Kind == kind)
                    return result;
                await ApprovePendingAsync();
                continue;
            }

            throw new InvalidOperationException($"Reached {result.Status} before the {kind} gate.");
        }

        throw new InvalidOperationException($"Never reached the {kind} gate.");
    }

    /// <summary>Resumes from the current committed state, approving every gate, to a terminal state.</summary>
    public async Task<AcademicWorkflowRunResult> DrainFromCurrentAsync()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var result = await Orchestrator.ResumeAsync(ProjectId, CancellationToken.None);
            switch (result.Status)
            {
                case AcademicWorkflowRunStatus.Completed:
                case AcademicWorkflowRunStatus.PausedBudget:
                case AcademicWorkflowRunStatus.Cancelled:
                case AcademicWorkflowRunStatus.ProjectFailed:
                    return result;
                case AcademicWorkflowRunStatus.AwaitingApproval:
                    await ApprovePendingAsync();
                    break;
            }
        }

        throw new InvalidOperationException("Drain from current never reached a terminal state.");
    }

    public async Task ApprovePendingAsync(string? notes = null)
    {
        var snapshot = await Store.LoadAsync(ProjectId, CancellationToken.None);
        var kind = snapshot?.PendingApproval?.Kind
            ?? throw new InvalidOperationException("No approval is pending.");
        await SubmitApprovalAsync(kind, AcademicApprovalDecision.Approved, notes);
    }

    public async Task SubmitApprovalAsync(AcademicApprovalKind kind, AcademicApprovalDecision decision, string? notes = null)
    {
        var snapshot = await Store.LoadAsync(ProjectId, CancellationToken.None);
        var pending = snapshot?.PendingApproval ?? throw new InvalidOperationException("No approval is pending.");
        if (pending.Kind != kind)
            throw new InvalidOperationException($"Pending gate is {pending.Kind}, not {kind}.");
        var submission = await Orchestrator.SubmitApprovalDecisionAsync(
            ProjectId, kind, pending.ApprovalKey, pending.CheckpointRevision, decision, notes,
            Guid.Parse("33333333-3333-3333-3333-333333333333"), CancellationToken.None);
        if (submission.Status != AcademicApprovalSubmissionStatus.Applied)
            throw new InvalidOperationException("Approval submission failed: " + submission.Status);
    }

    public AcademicWorkflowRunResult RunResultPlaceholder { get; } =
        AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.NotFound, null);
}

internal static class AcademicWorkflowFixtures
{
    public static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string S1 = "src-kurdistan-jama";
    public const string S2 = "src-medicines-book";
    public const string S3 = "src-krg-report";
    public const string S4 = "src-retracted";
    public const string AbstractS1 = "Pharmaceutical market access in Iraq improved after 2019 regulatory reforms.";
    public const string AbstractS2 = "This monograph surveys regulation of essential medicines across Middle Eastern health systems.";
    public static readonly string[] Outline = ["Introduction", "Literature Review", "Methodology", "Findings", "Discussion", "Conclusion"];

    public static AcademicWorkflowRequest Request(string requestKey) => new()
    {
        WorkspaceId = WorkspaceId,
        RequestKey = requestKey,
        ResearchQuestion = "How have market access and pricing regulations for pharmaceuticals evolved in the Kurdistan Region of Iraq?",
        Discipline = "Health Economics",
        Level = AcademicResearchLevel.Masters,
        Method = AcademicMethod.SystematicReview,
        CitationStyle = AcademicCitationStyle.Apa7,
        MaxDiscoverySources = 40,
        TargetChapterHeadings = ["Introduction", "Literature Review"],
        RequiredApprovals =
        [
            AcademicApprovalKind.Plan,
            AcademicApprovalKind.SourceLibrary,
            AcademicApprovalKind.Synthesis,
            AcademicApprovalKind.FinalVerification,
        ],
        MaxStageAttempts = 3,
    };

    public static AcademicResearchPlan Plan(int version = 1) => new()
    {
        PlanVersion = version,
        ResearchQuestion = Request("x").ResearchQuestion,
        Objectives = ["Map regulatory instruments 2015-2024", "Assess access outcomes for essential medicines"],
        InclusionCriteria = ["Peer-reviewed journal articles, scholarly books, theses and official health reports"],
        ExclusionCriteria = ["Non-scholarly websites and marketing brochures"],
        SearchStrategy =
        [
            new AcademicSearchSpecification { Catalog = AcademicCatalog.OpenAlex, Query = "pharmaceutical market access Kurdistan Iraq" },
            new AcademicSearchSpecification { Catalog = AcademicCatalog.Crossref, Query = "drug pricing regulation Iraq" },
            new AcademicSearchSpecification { Catalog = AcademicCatalog.OpenLibrary, Query = "essential medicines Middle East" },
        ],
        ChapterOutline = Outline,
    };

    public static List<AcademicSourceRecord> Sources() =>
    [
        new()
        {
            SourceId = S1,
            Title = "Market access for pharmaceuticals in Iraq: 2015-2021",
            Authors = ["Hama Karim", "Delovan Ahmed"],
            SourceType = "journal-article",
            AccessLevel = AcademicAccessLevel.FullText,
            DedupKey = "doi:10.1001/kurdhj.2020.3617",
            IdentifierKind = AcademicIdentifierKind.Doi,
            IdentifierValue = "10.1001/kurdhj.2020.3617",
            Publisher = "Kurdistan Medical Journal",
            PublicationYear = 2020,
            Url = "https://example.gov/kmj/2020-3617",
            AbstractText = AbstractS1,
        },
        new()
        {
            SourceId = S2,
            Title = "Essential Medicines Regulation in the Middle East",
            Authors = ["S. Qadir"],
            SourceType = "book",
            AccessLevel = AcademicAccessLevel.Abstract,
            DedupKey = "isbn:9780198808732",
            IdentifierKind = AcademicIdentifierKind.Isbn,
            IdentifierValue = "9780198808732",
            Publisher = "Academic Press",
            PublicationYear = 2019,
            AbstractText = AbstractS2,
        },
        new()
        {
            SourceId = S3,
            Title = "KRG Health Sector Annual Report 2021",
            Authors = ["KRG Ministry of Health"],
            SourceType = "report",
            AccessLevel = AcademicAccessLevel.MetadataOnly,
            DedupKey = "url:https://krg-health.example.gov/reports/market-access-2021",
            IdentifierKind = AcademicIdentifierKind.Url,
            IdentifierValue = "https://krg-health.example.gov/reports/market-access-2021",
            Url = "https://krg-health.example.gov/reports/market-access-2021",
            PublicationYear = 2021,
            AccessNote = "Repository API terms: metadata only, no text mining.",
        },
        new()
        {
            SourceId = S4,
            Title = "Drug pricing elasticity in regional markets (retracted)",
            Authors = ["A. Fabricator"],
            SourceType = "journal-article",
            AccessLevel = AcademicAccessLevel.FullText,
            DedupKey = "doi:10.1000/elasticity.2022",
            IdentifierKind = AcademicIdentifierKind.Doi,
            IdentifierValue = "10.1000/elasticity.2022",
            PublicationYear = 2022,
            IsRetracted = true,
            AccessNote = "Retraction notice 2023-04; retracted by publisher.",
        },
    ];

    public static List<AcademicScreeningDecisionRecord> Screening() =>
    [
        new() { SourceId = S1, Decision = AcademicScreeningDecision.Included, Reason = "Peer-reviewed study inside scope." },
        new() { SourceId = S2, Decision = AcademicScreeningDecision.Included, Reason = "Scholarly monograph covering the region." },
        new() { SourceId = S3, Decision = AcademicScreeningDecision.Included, Reason = "Official report; bibliographic reference only (metadata access)." },
        new() { SourceId = S4, Decision = AcademicScreeningDecision.Excluded, Reason = "Retracted by publisher." },
    ];

    public static List<AcademicEvidenceItem> Evidence() =>
    [
        new()
        {
            EvidenceId = "ev-1",
            SourceId = S1,
            Topic = "Access outcomes",
            Content = AbstractS1,
            Verbatim = true,
            Locator = "p. 12",
        },
        new()
        {
            EvidenceId = "ev-2",
            SourceId = S2,
            Topic = "Regulatory divergence",
            Content = "Regional regulation of essential medicines diverges across Middle Eastern health systems.",
            Verbatim = false,
        },
    ];

    public static List<AcademicTheme> Themes() =>
    [
        new()
        {
            ThemeId = "th-1",
            Statement = "Post-2019 reform is associated with improved pharmaceutical market access in Iraq.",
            SupportingEvidenceIds = ["ev-1", "ev-2"],
        },
        new()
        {
            ThemeId = "th-2",
            Statement = "Empirical evidence specific to the Kurdistan Region remains thin.",
            ResearchGaps = ["No KRG-specific pricing elasticity study found within the bounded search."],
        },
    ];

    public static List<AcademicChapterDraft> Chapters() =>
    [
        new()
        {
            ChapterId = "ch-1",
            Heading = "Introduction",
            Blocks =
            [
                new() { BlockId = "b-1", Kind = AcademicBlockKind.Heading, Text = "Introduction" },
                new()
                {
                    BlockId = "b-2",
                    Kind = AcademicBlockKind.Paragraph,
                    Text = "Market access for medicines in Iraq has shifted over the past decade.",
                    CitationSourceIds = [S1, S3],
                    Quotes = [new AcademicInlineQuote { SourceId = S1, QuotedText = AbstractS1, Locator = "p. 12" }],
                },
            ],
        },
    ];

    public static AcademicVerificationReport Verification(bool passed = true) => new()
    {
        Passed = passed,
        ChecksRun = AcademicIntegrityGuard.RequiredVerificationChecks.ToList(),
    };

    public static AcademicStageOutcome OutcomeFor(AcademicStage stage, AcademicWorkflowSnapshot snapshot, AcademicUsageRecord usage) =>
        stage switch
        {
            AcademicStage.Plan => AcademicStageOutcome.Completed(new AcademicStageOutput { Plan = Plan() }, usage),
            AcademicStage.Discover => AcademicStageOutcome.Completed(new AcademicStageOutput { Sources = Sources() }, usage),
            AcademicStage.Screen => AcademicStageOutcome.Completed(new AcademicStageOutput { Screening = Screening() }, usage),
            AcademicStage.Extract => AcademicStageOutcome.Completed(new AcademicStageOutput { Evidence = Evidence() }, usage),
            AcademicStage.Synthesize => AcademicStageOutcome.Completed(new AcademicStageOutput { Themes = Themes() }, usage),
            AcademicStage.DraftChapter => AcademicStageOutcome.Completed(new AcademicStageOutput { Chapters = Chapters() }, usage),
            AcademicStage.Verify => AcademicStageOutcome.Completed(new AcademicStageOutput { Verification = Verification() }, usage),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}

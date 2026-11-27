using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests.AcademicIntegration;

public sealed class AcademicPlanStageHandlerTests
{
    [Fact]
    public async Task Produces_a_bounded_reviewable_plan_from_the_saved_brief_without_external_usage()
    {
        var request = AcademicWorkflowFixtures.Request("plan-handler-test") with
        {
            ResearchQuestion = "How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq?",
            Discipline = "Pharmacy and Health Policy",
            Level = AcademicResearchLevel.Masters,
            Method = AcademicMethod.ScopingReview,
            CitationStyle = AcademicCitationStyle.Vancouver,
            WorkingTitle = "Pharmaceutical market access in the Kurdistan Region",
            GeographicScope = "Kurdistan Region of Iraq",
            PopulationOrContext = "Regulated medicines and patients",
            TimePeriod = "2012-2025",
            SubQuestions = ["Which institutions regulate medicine entry?", "What changed for market access?"],
            Keywords = ["pharmaceutical market access", "medicine regulation", "Kurdistan"],
            MinimumSources = 60,
            MinimumPeerReviewed = 40,
            AllowedSourceTypes = ["journal_article", "book", "book_chapter", "thesis"],
            RequireFullText = true,
            ProgrammeRequirements = "Use Vancouver style and include an auditable screening log.",
            TargetChapterHeadings = ["Introduction", "Regulatory Context", "Literature Review", "Methods", "Conclusion"],
        };
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var context = new AcademicStageContext
        {
            Snapshot = snapshot,
            Stage = AcademicStage.Plan,
            IdempotencyKey = "test-plan-stage-key",
            Attempt = 1,
        };
        var handler = new AcademicPlanStageHandler();

        var estimate = handler.Estimate(context);
        Assert.Equal(0m, estimate.EstimatedCostUsd);
        Assert.Equal(0, estimate.EstimatedProviderCalls);

        var outcome = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0m, outcome.Usage.CostUsd);
        Assert.Equal(0, outcome.Usage.ProviderCalls);
        Assert.Null(AcademicWorkflowOrchestrator.ValidateOutputContract(AcademicStage.Plan, outcome.Output));
        var plan = Assert.IsType<AcademicResearchPlan>(outcome.Output!.Plan);
        Assert.Equal(request.ResearchQuestion, plan.ResearchQuestion);
        Assert.Equal(AcademicResearchLevel.Masters, plan.Level);
        Assert.Equal(AcademicMethod.ScopingReview, plan.Method);
        Assert.Equal(AcademicCitationStyle.Vancouver, plan.CitationStyle);
        Assert.Equal(new[] { AcademicMethod.ScopingReview }, plan.Methodologies);
        Assert.Equal(request.WorkingTitle, plan.WorkingTitle);
        Assert.Equal(request.GeographicScope, plan.GeographicScope);
        Assert.Equal(request.PopulationOrContext, plan.PopulationOrContext);
        Assert.Equal(request.TimePeriod, plan.TimePeriod);
        Assert.Equal(request.SubQuestions, plan.SubQuestions);
        Assert.Equal(request.Keywords, plan.Keywords);
        Assert.Equal(60, plan.MinimumSources);
        Assert.Equal(40, plan.MinimumPeerReviewed);
        Assert.Equal(request.AllowedSourceTypes, plan.AllowedSourceTypes);
        Assert.True(plan.RequireFullText);
        Assert.Equal(request.ProgrammeRequirements, plan.ProgrammeRequirements);
        Assert.Equal(request.TargetChapterHeadings, plan.ChapterOutline);
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("scholarly books", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("journal articles", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("full text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("instructions embedded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("exact edition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("sibling edition", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, plan.SearchStrategy.Count);
        Assert.All(plan.SearchStrategy, search =>
        {
            Assert.True(search.Query.Length <= 500);
            Assert.Contains("Kurdistan", search.Query, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("Kurdistan Region of Iraq", plan.SearchStrategy[0].Query, StringComparison.Ordinal);
        Assert.Contains("Regulated medicines and patients", plan.SearchStrategy[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_source_types_replace_the_default_book_and_article_recommendation()
    {
        var request = AcademicWorkflowFixtures.Request("restricted-source-types") with
        {
            ResearchQuestion = "How do institutional reports describe medicine market access?",
            AllowedSourceTypes = ["report"],
        };
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var context = new AcademicStageContext
        {
            Snapshot = snapshot,
            Stage = AcademicStage.Plan,
            IdempotencyKey = "restricted-source-types-plan",
            Attempt = 1,
        };

        var outcome = await new AcademicPlanStageHandler().ExecuteAsync(context, CancellationToken.None);
        var plan = Assert.IsType<AcademicResearchPlan>(outcome.Output!.Plan);

        Assert.Contains(plan.InclusionCriteria, criterion => criterion.Contains("reports and grey literature", StringComparison.Ordinal));
        Assert.Contains(plan.Objectives, objective => objective.Contains("reports and grey literature", StringComparison.Ordinal));
        Assert.Contains(plan.SearchStrategy.SelectMany(search => search.Filters), filter => filter.Contains("reports and grey literature", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Objectives, objective => objective.Contains("scholarly books", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Objectives, objective => objective.Contains("journal articles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.InclusionCriteria, criterion => criterion.Contains("scholarly books", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.InclusionCriteria, criterion => criterion.Contains("journal articles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.SearchStrategy.SelectMany(search => search.Filters), filter => filter.Contains("journal articles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Long_questions_are_split_without_losing_late_keywords_or_scope_terms()
    {
        var longQuestion = string.Join(" ", Enumerable.Range(0, 90).Select(index => $"research-term-{index}")) + " question-tail-marker";
        var request = AcademicWorkflowFixtures.Request("long-query-plan") with
        {
            ResearchQuestion = longQuestion,
            Keywords = ["reimbursement", "market-access-keyword"],
            GeographicScope = "Kurdistan Region of Iraq",
            PopulationOrContext = "Regulated medicines and patients across the Kurdistan Region",
            TimePeriod = "2012-2025",
        };
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var context = new AcademicStageContext
        {
            Snapshot = snapshot,
            Stage = AcademicStage.Plan,
            IdempotencyKey = "long-query-plan-stage",
            Attempt = 1,
        };

        var outcome = await new AcademicPlanStageHandler().ExecuteAsync(context, CancellationToken.None);
        var plan = Assert.IsType<AcademicResearchPlan>(outcome.Output!.Plan);
        var queries = plan.SearchStrategy.Select(search => search.Query).ToArray();

        Assert.True(queries.Length > 3);
        Assert.All(queries, query => Assert.InRange(query.Length, 1, 500));
        var allQueries = string.Join(" ", queries);
        Assert.Contains("research-term-0", allQueries, StringComparison.Ordinal);
        Assert.Contains("question-tail-marker", allQueries, StringComparison.Ordinal);
        Assert.Contains("market-access-keyword", allQueries, StringComparison.Ordinal);
        Assert.Contains("Kurdistan Region of Iraq", allQueries, StringComparison.Ordinal);
        Assert.Contains("Regulated medicines and patients across the Kurdistan Region", allQueries, StringComparison.Ordinal);
        Assert.Contains("2012-2025", allQueries, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Honors_request_cancellation_before_building_a_plan()
    {
        var request = AcademicWorkflowFixtures.Request("cancelled-plan-test");
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var context = new AcademicStageContext
        {
            Snapshot = snapshot,
            Stage = AcademicStage.Plan,
            IdempotencyKey = "cancelled-plan-stage-key",
            Attempt = 1,
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AcademicPlanStageHandler().ExecuteAsync(context, cancellation.Token));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.AcademicIntegration;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;
using Taslim.Api.Infrastructure;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests.AcademicIntegration;

/// <summary>Opt in only this test host to the deterministic Plan handler.</summary>
public sealed class AcademicPlanApiFactory : TaslimApiFactory
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?> { [AcademicWorkflowOptions.PlanEnabledKey] = "true" }));
        return base.CreateHost(builder);
    }
}

/// <summary>
/// The integration surface with the API Plan switch enabled: authenticated and
/// workspace-scoped, with one deterministic local Plan handler. These tests pin the
/// honest staging contract — intake persists and replays idempotently, Plan is saved and
/// gated for approval, and the next missing stage remains an explicit resumable 501.
/// </summary>
public class AcademicProjectsApiTests : IClassFixture<AcademicPlanApiFactory>
{
    private readonly TaslimApiFactory _factory;

    public AcademicProjectsApiTests(AcademicPlanApiFactory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Every_route_requires_authentication()
    {
        using var client = _factory.CreateClient();
        var workspace = Guid.NewGuid();
        var project = Guid.NewGuid();
        foreach (var (method, path) in new (string, string)[]
        {
            ("GET", $"/api/workspaces/{workspace}/academic-projects"),
            ("POST", $"/api/workspaces/{workspace}/academic-projects"),
            ("GET", $"/api/academic-projects/{project}"),
            ("POST", $"/api/academic-projects/{project}/run"),
            ("POST", $"/api/academic-projects/{project}/recover-abandoned"),
            ("POST", $"/api/academic-projects/{project}/approvals/plan"),
            ("POST", $"/api/academic-projects/{project}/budget"),
            ("GET", $"/api/academic-projects/{project}/evidence-matrix"),
            ("GET", $"/api/academic-projects/{project}/verification"),
            ("GET", $"/api/academic-projects/{project}/checkpoints"),
        })
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Create_persists_intake_and_reports_honest_stage_connectivity()
    {
        var (client, workspaceId) = await RegisterAsync();
        var response = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"connectivity-{Guid.NewGuid():N}",
            researchQuestion = "How has antibiotic stewardship policy changed in EU member states since 2015?",
            discipline = "Public Health Policy",
            level = "doctoral",
            method = "systematicreview",
            citationStyle = "harvard",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var project = dto.GetProperty("project");
        Assert.Equal("Running", project.GetProperty("state").GetString());
        Assert.Equal(0, project.GetProperty("revision").GetInt64());
        Assert.Equal("Doctoral", project.GetProperty("level").GetString());
        Assert.Equal(6, dto.GetProperty("missingStages").GetArrayLength());
        Assert.Equal(1, dto.GetProperty("connectedStages").GetArrayLength());
        Assert.Equal("Plan", dto.GetProperty("connectedStages")[0].GetString());
        Assert.Equal(0, dto.GetProperty("planVersion").GetInt32());
        Assert.Equal(0, dto.GetProperty("sourceCount").GetInt32());
        // Nothing is projectable yet, so the derived tables are vacuously current; the
        // flag only turns false in the connected-flow tests when a commit outruns it.
        Assert.True(dto.GetProperty("evidenceCurrent").GetBoolean());

        // The row exists in the shared context with the intake checkpoint recorded.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var projectId = project.GetProperty("projectId").GetGuid();
        var row = await db.Set<AcademicWorkflowProjectEntity>().AsNoTracking()
            .SingleAsync(e => e.ProjectId == projectId);
        Assert.Equal(workspaceId, row.WorkspaceId);
        Assert.Equal(0, row.Revision);
        Assert.Equal(-1, row.EvidenceProjectedRevision);
        var checkpoints = await db.Set<AcademicWorkflowCheckpointEntity>().AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        Assert.Single(checkpoints);
        Assert.Equal(0, checkpoints.Single().Revision);
    }

    [Fact]
    public void Enabled_composition_registers_only_the_deterministic_plan_handler()
    {
        using var scope = _factory.Services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IAcademicStageHandler>().ToArray();

        var handler = Assert.Single(handlers);
        Assert.IsType<AcademicPlanStageHandler>(handler);
        Assert.Equal(AcademicStage.Plan, handler.Stage);
    }

    [Fact]
    public async Task Replayed_request_key_resumes_the_same_project_without_new_rows_or_charges()
    {
        var (client, workspaceId) = await RegisterAsync();
        var requestKey = $"replay-{Guid.NewGuid():N}";
        var payload = new
        {
            requestKey,
            researchQuestion = "Replay question",
            discipline = "History",
        };

        var first = await PostCreateAsync(client, workspaceId, payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        // Both a body request key and an Idempotency-Key header resolve the same intake.
        var second = await PostCreateAsync(client, workspaceId, new
        {
            researchQuestion = "Replay question",
            discipline = "History",
        }, requestKey);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();
        Assert.Equal(firstId, secondId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.Set<AcademicWorkflowProjectEntity>().CountAsync(e => e.ProjectId == firstId));
        Assert.Equal(1, await db.Set<AcademicWorkflowCheckpointEntity>().CountAsync(e => e.ProjectId == firstId));
    }

    [Fact]
    public async Task Invalid_intake_is_rejected_and_budget_ceilings_fail_closed()
    {
        var (client, workspaceId) = await RegisterAsync();

        var noKey = await PostCreateAsync(client, workspaceId, new { researchQuestion = "x", discipline = "y" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var noQuestion = await PostCreateAsync(client, workspaceId, new { requestKey = "k1", discipline = "y" });
        Assert.Equal(HttpStatusCode.BadRequest, noQuestion.StatusCode);

        var oversizeBudget = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = "k2",
            researchQuestion = "x",
            discipline = "y",
            budget = new { maxTotalUsd = 99_999m },
        });
        Assert.Equal(HttpStatusCode.BadRequest, oversizeBudget.StatusCode);
        Assert.Contains("ACADEMIC_REQUEST_INVALID", await oversizeBudget.Content.ReadAsStringAsync());

        var stageOverTotal = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = "k3",
            researchQuestion = "x",
            discipline = "y",
            budget = new { maxTotalUsd = 5m, maxStageUsd = 9m },
        });
        Assert.Equal(HttpStatusCode.BadRequest, stageOverTotal.StatusCode);

        var tooManyKeywords = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"too-many-keywords-{Guid.NewGuid():N}",
            researchQuestion = "A valid academic research question",
            discipline = "Public Health",
            keywords = Enumerable.Range(1, 13).Select(index => $"keyword-{index}").ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooManyKeywords.StatusCode);
        Assert.Contains("ACADEMIC_REQUEST_INVALID", await tooManyKeywords.Content.ReadAsStringAsync());

        var withoutPlanGate = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = "missing-plan-gate",
            researchQuestion = "A valid academic research question",
            discipline = "Public Health",
            requiredApprovals = new[] { "SourceLibrary" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withoutPlanGate.StatusCode);
        Assert.Contains("Plan approval checkpoint is required", await withoutPlanGate.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("false", 0)]
    [InlineData("invalid", 0)]
    [InlineData("true", 1)]
    public void Academic_composition_registers_plan_only_on_explicit_opt_in(string? configured, int expected)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [AcademicWorkflowOptions.PlanEnabledKey] = configured }).Build();

        services.AddAcademicProjects(configuration);
        services.AddAcademicProjects(configuration);

        Assert.Equal(expected, services.Count(descriptor => descriptor.ServiceType == typeof(IAcademicStageHandler)));
    }

    [Fact]
    public async Task Default_api_preserves_intake_and_blocks_plan_execution_without_advancing()
    {
        using var defaultHost = new TaslimApiFactory();
        using var scope = defaultHost.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var defaultClient = defaultHost.CreateClient();
        var (client, workspace) = await RegisterAsync(defaultClient);
        var created = await PostCreateAsync(client, workspace, new
        {
            requestKey = "academic-off-test",
            researchQuestion = "How are sources catalogued?",
            discipline = "History",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var intake = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(intake.GetProperty("connectedStages").EnumerateArray());
        var project = intake.GetProperty("project");
        var path = $"/api/academic-projects/{project.GetProperty("projectId").GetGuid()}";

        var run = await PostJsonAsync(client, $"{path}/run", new { });
        Assert.Equal(HttpStatusCode.NotImplemented, run.StatusCode);
        var error = (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("ACADEMIC_STAGE_NOT_CONNECTED", error.GetProperty("code").GetString());
        Assert.Equal("Plan", error.GetProperty("missingStage").GetString());
        var detail = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(project.GetProperty("revision").GetInt64(), detail.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal(0, detail.GetProperty("planVersion").GetInt32());
        Assert.Equal(0, detail.GetProperty("usage").GetProperty("callsUsed").GetInt32());
    }

    [Fact]
    public async Task Cross_tenant_access_is_rejected_and_foreign_projects_do_not_appear()
    {
        var (ownerClient, ownerWorkspace) = await RegisterAsync();
        var create = await PostCreateAsync(ownerClient, ownerWorkspace, new
        {
            requestKey = $"isolation-{Guid.NewGuid():N}",
            researchQuestion = "Isolation probe",
            discipline = "Law",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var (otherClient, otherWorkspace) = await RegisterAsync();

        // The other student's list never sees it, whatever they route through.
        var list = await otherClient.GetAsync($"/api/workspaces/{otherWorkspace}/academic-projects");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty(JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray());

        // Every project route must make a foreign project indistinguishable from an
        // unknown project id, without weakening workspace authorization elsewhere.
        foreach (var suffix in new[]
        {
            "",
            "/evidence-matrix",
            "/verification",
            "/checkpoints",
            "/run",
            "/approvals/plan",
            "/budget",
        })
        {
            async Task<HttpResponseMessage> SendProbeAsync(Guid id)
            {
                var path = $"/api/academic-projects/{id}{suffix}";
                return suffix is "/run" or "/approvals/plan" or "/budget"
                    ? await PostJsonAsync(otherClient, path, new { })
                    : await otherClient.GetAsync(path);
            }

            using var foreign = await SendProbeAsync(projectId);
            using var unknown = await SendProbeAsync(Guid.NewGuid());
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            var foreignBody = await foreign.Content.ReadAsStringAsync();
            var unknownBody = await unknown.Content.ReadAsStringAsync();
            Assert.Equal(unknownBody, foreignBody);
            var error = JsonDocument.Parse(foreignBody).RootElement.GetProperty("error");
            Assert.Equal("PROJECT_NOT_FOUND", error.GetProperty("code").GetString());
            Assert.Equal("Project not found.", error.GetProperty("message").GetString());
        }

        // And they cannot create into someone else's workspace.
        var foreignCreate = await PostCreateAsync(otherClient, ownerWorkspace, new
        {
            requestKey = "intruder",
            researchQuestion = "intrude",
            discipline = "x",
        });
        Assert.Equal(HttpStatusCode.Forbidden, foreignCreate.StatusCode);
    }

    [Theory]
    [InlineData(40, 40, true)]
    [InlineData(41, null, false)]
    [InlineData(null, 40, true)]
    [InlineData(null, 41, false)]
    [InlineData(10, 11, false)]
    public async Task Source_targets_must_fit_the_projects_discovery_limit(int? total, int? peerReviewed, bool accepted)
    {
        var (client, workspaceId) = await RegisterAsync();
        var response = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"targets-{Guid.NewGuid():N}",
            researchQuestion = "Which sources discuss the research question?",
            discipline = "History",
            maxDiscoverySources = 40,
            minimumSources = total,
            minimumPeerReviewed = peerReviewed,
        });

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        if (!accepted)
        {
            Assert.Contains("ACADEMIC_REQUEST_INVALID", await response.Content.ReadAsStringAsync());
            var projects = await client.GetFromJsonAsync<JsonElement[]>($"/api/workspaces/{workspaceId}/academic-projects");
            Assert.Empty(projects!);
        }
    }

    [Fact]
    public async Task Plan_lists_reject_overflow_and_preserve_every_accepted_entry_after_reload()
    {
        var (client, workspaceId) = await RegisterAsync();
        var questions = Enumerable.Range(1, 4).Select(i => $"Research sub-question {i}?").ToArray();
        var longHeading = new string('h', 200);
        var headings = Enumerable.Range(1, 23).Select(i => $"Chapter {i}").Append(longHeading).ToArray();
        var request = new
        {
            requestKey = $"plan-lists-{Guid.NewGuid():N}",
            researchQuestion = "How are records used in historical research?",
            discipline = "History",
            subQuestions = questions,
            targetChapterHeadings = headings,
        };

        foreach (var invalid in new[]
        {
            request with { subQuestions = [.. questions, "Which additional sources are needed?"] },
            request with { targetChapterHeadings = [.. headings, "One chapter too many"] },
        })
        {
            var rejected = await PostCreateAsync(client, workspaceId, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("ACADEMIC_REQUEST_INVALID", await rejected.Content.ReadAsStringAsync());
        }
        var projects = await client.GetFromJsonAsync<JsonElement[]>($"/api/workspaces/{workspaceId}/academic-projects");
        Assert.Empty(projects!);

        var accepted = await PostCreateAsync(client, workspaceId, request);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var projectId = (await accepted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();
        var path = $"/api/academic-projects/{projectId}";
        var run = await PostJsonAsync(client, $"{path}/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var reloaded = await client.GetFromJsonAsync<JsonElement>(path);
        var plan = reloaded.GetProperty("plan");
        Assert.Equal(questions, plan.GetProperty("subQuestions").Deserialize<string[]>());
        Assert.Equal(headings, plan.GetProperty("chapterOutline").Deserialize<string[]>());
    }

    [Fact]
    public async Task Run_saves_and_gates_the_plan_then_reports_missing_discovery_as_resumable()
    {
        var (client, workspaceId) = await RegisterAsync();
        var create = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"not-connected-{Guid.NewGuid():N}",
            researchQuestion = "How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq since 2012?",
            discipline = "Pharmacy and Health Policy",
            level = "Masters",
            method = "SystematicReview",
            methodologies = new[] { "SystematicReview", "Qualitative" },
            citationStyle = "Vancouver",
            workingTitle = "Pharmaceutical market access in the Kurdistan Region",
            geographicScope = "Kurdistan Region of Iraq",
            populationOrContext = "Regulated medicines and patients",
            timePeriod = "2012-2025",
            subQuestions = new[] { "Which institutions regulate medicine entry?" },
            keywords = new[] { "pharmaceutical market access", "medicine regulation" },
            minimumSources = 60,
            minimumPeerReviewed = 40,
            allowedSourceTypes = new[] { "journal_article", "book" },
            requireFullText = true,
            programmeRequirements = "Use Vancouver style and keep an auditable screening log.",
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var run = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var prepared = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingApproval", prepared.GetProperty("project").GetProperty("state").GetString());
        Assert.Equal("Plan", prepared.GetProperty("pendingApproval").GetProperty("kind").GetString());
        Assert.Equal(1, prepared.GetProperty("planVersion").GetInt32());
        Assert.Equal(1, prepared.GetProperty("connectedStages").GetArrayLength());
        Assert.Equal(6, prepared.GetProperty("missingStages").GetArrayLength());

        var prematureResume = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.Equal(HttpStatusCode.OK, prematureResume.StatusCode);
        var heldForReview = await prematureResume.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingApproval", heldForReview.GetProperty("project").GetProperty("state").GetString());
        Assert.Equal("Plan", heldForReview.GetProperty("pendingApproval").GetProperty("kind").GetString());
        Assert.Equal(
            prepared.GetProperty("project").GetProperty("revision").GetInt64(),
            heldForReview.GetProperty("project").GetProperty("revision").GetInt64());

        var plan = prepared.GetProperty("plan");
        Assert.Equal("How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq since 2012?", plan.GetProperty("researchQuestion").GetString());
        Assert.Equal("Masters", plan.GetProperty("level").GetString());
        Assert.Equal("Vancouver", plan.GetProperty("citationStyle").GetString());
        Assert.Equal(2, plan.GetProperty("methodologies").GetArrayLength());
        Assert.Equal("Qualitative", plan.GetProperty("methodologies")[1].GetString());
        Assert.Equal("Kurdistan Region of Iraq", plan.GetProperty("geographicScope").GetString());
        Assert.Equal("2012-2025", plan.GetProperty("timePeriod").GetString());
        Assert.True(plan.GetProperty("requireFullText").GetBoolean());
        Assert.Equal(60, plan.GetProperty("minimumSources").GetInt32());
        Assert.Equal(40, plan.GetProperty("minimumPeerReviewed").GetInt32());
        Assert.Equal("pharmaceutical market access", plan.GetProperty("keywords")[0].GetString());
        Assert.Equal("medicine regulation", plan.GetProperty("keywords")[1].GetString());
        Assert.Contains(plan.GetProperty("inclusionCriteria").EnumerateArray(), item =>
            item.GetString()!.Contains("scholarly books", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.GetProperty("inclusionCriteria").EnumerateArray(), item =>
            item.GetString()!.Contains("journal articles", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, plan.GetProperty("searchStrategy").GetArrayLength());
        Assert.Equal("ScholarlyRecords", plan.GetProperty("searchStrategy")[0].GetProperty("collection").GetString());
        Assert.Equal("JournalAndIdentifierRecords", plan.GetProperty("searchStrategy")[1].GetProperty("collection").GetString());
        Assert.Equal("BookRecords", plan.GetProperty("searchStrategy")[2].GetProperty("collection").GetString());
        Assert.Contains(plan.GetProperty("allowedSourceTypes").EnumerateArray(), item => item.GetString() == "book");
        Assert.Contains(plan.GetProperty("allowedSourceTypes").EnumerateArray(), item => item.GetString() == "journal_article");
        Assert.DoesNotContain("OpenAlex", plan.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Crossref", plan.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prepared.GetProperty("usage").GetProperty("spentUsd").GetDecimal());
        Assert.Equal(0, prepared.GetProperty("usage").GetProperty("callsUsed").GetInt32());

        var pendingApproval = heldForReview.GetProperty("pendingApproval");
        var approve = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/approvals/plan", new
        {
            decision = "Approved",
            approvalKey = pendingApproval.GetProperty("approvalKey").GetString(),
            checkpointRevision = pendingApproval.GetProperty("checkpointRevision").GetInt64(),
        });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = await approve.Content.ReadFromJsonAsync<JsonElement>();
        var approvedRevision = approved.GetProperty("project").GetProperty("revision").GetInt64();
        var checkpointsBeforeMissingStage = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        var missingRun = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.Equal(HttpStatusCode.NotImplemented, missingRun.StatusCode);
        var error = (await missingRun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("ACADEMIC_STAGE_NOT_CONNECTED", error.GetProperty("code").GetString());
        Assert.Equal("Discover", error.GetProperty("missingStage").GetString());
        Assert.Equal(1, error.GetProperty("connectedStages").GetArrayLength());
        Assert.Equal("Plan", error.GetProperty("connectedStages")[0].GetString());

        // After the review checkpoint, the missing stage is reported without advancing
        // revision or turning the still-resumable project into a terminal failure.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/academic-projects/{projectId}");
        Assert.Equal("Running", detail.GetProperty("project").GetProperty("state").GetString());
        Assert.Equal(approvedRevision, detail.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal(1, detail.GetProperty("planVersion").GetInt32());
        Assert.Equal(0, detail.GetProperty("usage").GetProperty("spentUsd").GetDecimal());
        var checkpoints = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        Assert.NotNull(checkpoints);
        Assert.Equal(checkpointsBeforeMissingStage!.Length, checkpoints.Length);
    }

    [Fact]
    public async Task Undefined_numeric_approval_decision_is_rejected_without_moving_the_pending_checkpoint()
    {
        var (client, workspaceId) = await RegisterAsync();
        var create = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"invalid-decision-{Guid.NewGuid():N}",
            researchQuestion = "Approval decision validation probe",
            discipline = "Sociology",
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var run = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var pending = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingApproval", pending.GetProperty("project").GetProperty("state").GetString());
        var revision = pending.GetProperty("project").GetProperty("revision").GetInt64();
        var checkpointRevision = pending.GetProperty("pendingApproval").GetProperty("checkpointRevision").GetInt64();
        var approvalKey = pending.GetProperty("pendingApproval").GetProperty("approvalKey").GetString();

        var invalid = await PostJsonAsync(
            client,
            $"/api/academic-projects/{projectId}/approvals/plan",
            new { decision = "2147483647", notes = "must not be recorded", approvalKey, checkpointRevision });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/academic-projects/{projectId}");
        Assert.Equal("AwaitingApproval", unchanged.GetProperty("project").GetProperty("state").GetString());
        Assert.Equal(revision, unchanged.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal("Plan", unchanged.GetProperty("pendingApproval").GetProperty("kind").GetString());
        Assert.Equal(checkpointRevision, unchanged.GetProperty("pendingApproval").GetProperty("checkpointRevision").GetInt64());
        Assert.Empty(unchanged.GetProperty("approvals").EnumerateArray());

        var valid = await PostJsonAsync(
            client,
            $"/api/academic-projects/{projectId}/approvals/plan",
            new { decision = "Approved", approvalKey, checkpointRevision });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var approved = await valid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APPLIED", approved.GetProperty("code").GetString());
        Assert.Equal("Running", approved.GetProperty("project").GetProperty("state").GetString());
        Assert.Single(approved.GetProperty("approvals").EnumerateArray());
    }

    [Fact]
    public async Task Approval_without_a_pending_checkpoint_is_a_conflict_not_a_fabrication()
    {
        var (client, workspaceId) = await RegisterAsync();
        var create = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"no-pending-{Guid.NewGuid():N}",
            researchQuestion = "Approval probe",
            discipline = "Sociology",
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var approve = await PostJsonAsync(
            client,
            $"/api/academic-projects/{projectId}/approvals/plan",
            new
            {
                decision = "Approved",
                notes = "looks good",
                approvalKey = "missing-checkpoint",
                checkpointRevision = 1L,
            });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Contains("NO_PENDING_APPROVAL", await approve.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mutations_require_the_csrf_token()
    {
        var (client, workspaceId) = await RegisterAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/academic-projects")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { requestKey = "no-csrf", researchQuestion = "x", discipline = "y" }),
                Encoding.UTF8, "application/json"),
        };
        // Cookie is attached by the client handler, but the CSRF header is absent.
        var response = await client.SendAsync(request);
        Assert.False(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// The abuse-boundary contract for this lane's mutations, asserted the same way
    /// ApiAbuseBoundaryTests pins the older controllers: every POST that moves academic
    /// project state names an existing platform rate policy and still requires the CSRF
    /// header. Intake and checkpoint decisions ride the generation policy, a resume rides
    /// the expensive-AI policy (it is the route that spends stage work once lanes connect),
    /// and a spend-ceiling raise rides account security. Reads must stay unannotated: the
    /// matrix/verification/checkpoint surfaces are polling endpoints under the global
    /// limiter only.
    /// </summary>
    [Theory]
    [InlineData(nameof(AcademicProjectsController.Create), RateLimiting.Generation)]
    [InlineData(nameof(AcademicProjectsController.Run), RateLimiting.ExpensiveAi)]
    [InlineData(nameof(AcademicProjectsController.Approve), RateLimiting.Generation)]
    [InlineData(nameof(AcademicProjectsController.RaiseBudget), RateLimiting.AccountSecurity)]
    public void Academic_project_mutations_carry_the_existing_rate_policies(string actionName, string expectedPolicy)
    {
        var action = typeof(AcademicProjectsController).GetMethod(
            actionName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpPostAttribute>(inherit: true));
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
        var policy = action.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true);
        Assert.NotNull(policy);
        Assert.Equal(expectedPolicy, policy!.PolicyName);
    }

    [Fact]
    public void Academic_project_reads_stay_on_the_global_limiter()
    {
        var annotated = typeof(AcademicProjectsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpGetAttribute>(inherit: true).Any())
            .Select(method => method.Name)
            .ToArray();
        Assert.NotEmpty(annotated);
        foreach (var name in annotated)
        {
            var action = typeof(AcademicProjectsController).GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.Null(action!.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true));
        }
    }

    [Fact]
    public async Task Responses_never_leak_backend_service_or_billing_vocabulary()
    {
        var (client, workspaceId) = await RegisterAsync();
        var create = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"privacy-{Guid.NewGuid():N}",
            researchQuestion = "Privacy scan for the academic integration surface.",
            discipline = "Information Security",
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var bodies = new List<string>
        {
            await create.Content.ReadAsStringAsync(),
            await SafeBody(await client.GetAsync($"/api/workspaces/{workspaceId}/academic-projects")),
            await SafeBody(await client.GetAsync($"/api/academic-projects/{projectId}")),
            await SafeBody(await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { })),
            await SafeBody(await client.GetAsync($"/api/academic-projects/{projectId}/evidence-matrix")),
            await SafeBody(await client.GetAsync($"/api/academic-projects/{projectId}/verification")),
            await SafeBody(await client.GetAsync($"/api/academic-projects/{projectId}/checkpoints")),
        };

        foreach (var token in new[] { "openai", "gpt", "claude", "provider", "billing", "credits", "routing", "endpoint", "api key" })
        {
            foreach (var body in bodies)
            {
                Assert.DoesNotContain(token, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task List_is_workspace_scoped_and_paginated()
    {
        var (client, workspaceId) = await RegisterAsync();
        for (var i = 0; i < 3; i++)
        {
            var created = await PostCreateAsync(client, workspaceId, new
            {
                requestKey = $"paging-{Guid.NewGuid():N}",
                researchQuestion = $"Paging question {i}",
                discipline = "Education",
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var page = await client.GetAsync($"/api/workspaces/{workspaceId}/academic-projects?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("2", page.Headers.GetValues("X-Page-Size").Single());
        Assert.Equal("3", page.Headers.GetValues("X-Total-Count").Single());
        Assert.Equal(2, JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<(HttpClient Client, Guid WorkspaceId)> RegisterAsync(HttpClient? client = null)
    {
        client ??= _factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var token = csrf.GetProperty("token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    displayName = "Academic Integration Tester",
                    email = $"academic-integration-{Guid.NewGuid():N}@example.com",
                    password = "StrongPassword!123",
                    preferredLanguage = "en",
                }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        return (client, auth.PersonalWorkspace.Id);
    }

    private async Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client,
        Guid workspaceId,
        object payload,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/academic-projects")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await SendWithCsrfAsync(client, request);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        return await SendWithCsrfAsync(client, request);
    }

    private async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpRequestMessage request)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private StringContent CsrfJson(HttpClient client, string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<string> SafeBody(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();
}

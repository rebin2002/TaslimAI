using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Persistence;
using Taslim.Api.AcademicIntegration;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Taslim.Api.Tests;
using Xunit;

namespace Taslim.Api.Tests.AcademicIntegration;

/// <summary>
/// The same API surface driven with every stage handler wired, so the tests exercise the
/// integration contract end to end for real: checkpointed resumable execution through the
/// Phase C engine, approval gates, budget ceilings, and the projection into the Phase B
/// evidence tables. Handlers are the deterministic scripted doubles from the Phase C test
/// support — no network, no provider — and the clock advances one second per read, so
/// revisions, watermark movement and cell contents are all reproducible run to run.
/// </summary>
public sealed class AcademicConnectedApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AcademicConnectedApiFactory()
    {
        _connection.Open();
        StageHandlers = AcademicStageOrder.Stages
            .Select(stage => new ScriptedStageHandler(stage))
            .ToDictionary(handler => handler.Stage);
    }

    // internal: ScriptedStageHandler is test-support internal; consumers live in this assembly.
    internal Dictionary<AcademicStage, ScriptedStageHandler> StageHandlers { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(_connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(_connection));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new SteadyTimeProvider()));
            foreach (var handler in StageHandlers.Values)
            {
                // Exactly the seam provider lanes use, exercised here as its contract test.
                services.AddSingleton<IAcademicStageHandler>(handler);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public sealed class AcademicProjectsConnectedFlowTests : IClassFixture<AcademicConnectedApiFactory>
{
    private readonly AcademicConnectedApiFactory _factory;

    public AcademicProjectsConnectedFlowTests(AcademicConnectedApiFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Full_gated_run_completes_projects_and_projects_the_evidence_matrix()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "connected-happy",
            "How have market access and pricing regulations for pharmaceuticals evolved in Kurdistan?");

        // The fixture shares handlers across tests (parallelization is disabled assembly-wide),
        // so every call-count assertion below is a delta from this baseline.
        var baseline = _factory.StageHandlers.ToDictionary(kv => kv.Key, kv => kv.Value.Calls.Count);

        // Plan runs to its gate; sources are not there yet, so the matrix stays unprojected.
        var first = await RunAsync(client, projectId);
        Assert.Equal("AwaitingApproval", State(first));
        Assert.Equal("Plan", PendingKind(first));
        Assert.Equal(1, Artifact(first, "planVersion"));
        Assert.Equal(0, Artifact(first, "sourceCount"));

        var runCount = 0;
        while (State(first) is "Running" or "AwaitingApproval" && runCount < 32)
        {
            if (PendingKind(first) is string kind)
            {
                var approval = await ApproveAsync(client, projectId, kind);
                Assert.Equal("APPLIED", approval.GetProperty("code").GetString());
            }

            first = await RunAsync(client, projectId);
            runCount++;
        }

        Assert.Equal("Completed", State(first));
        Assert.True(first.GetProperty("project").GetProperty("revision").GetInt64() > 0);

        // Every stage executed exactly once across the whole gated run: no checkpoint in
        // the ledger re-ran, and no billable attempt was spent twice.
        foreach (var handler in _factory.StageHandlers.Values)
            Assert.Equal(baseline[handler.Stage] + 1, handler.Calls.Count);

        // Budget accounting: one metered entry per stage attempt, charged within ceilings.
        var usage = first.GetProperty("usage");
        Assert.Equal(3.5m, usage.GetProperty("spentUsd").GetDecimal());
        Assert.Equal(14, usage.GetProperty("callsUsed").GetInt32());

        // The matrix, cells, provenance and screening rows landed in the Phase B tables,
        // scoped to this workspace and project, and the API reads them back.
        var matrix = await client.GetFromJsonAsync<JsonElement>($"/api/academic-projects/{projectId}/evidence-matrix");
        Assert.True(matrix.GetProperty("projected").GetBoolean());
        Assert.True(matrix.GetProperty("cells").GetArrayLength() > 0);
        // Findings are content-dependent (gaps/contradictions), so only their shape is
        // pinned here: every finding row is one the matrix builder derived, never invented.
        Assert.All(matrix.GetProperty("findings").EnumerateArray(), finding =>
            Assert.Contains(finding.GetProperty("findingType").GetString(), new[] { "gap", "relationship" }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(
            matrix.GetProperty("cells").GetArrayLength(),
            await db.Set<AcademicMatrixCellEntity>().CountAsync(c => c.ProjectId == projectId && c.WorkspaceId == workspaceId));

        // Citation-verification boundary across the seam: no matrix cell claims a
        // verifiable locator, because no workflow source handed the evidence lane a
        // retrieved copy to anchor a pinpoint to. Metadata-only access never carries
        // support, and the retracted source is excluded, not silently kept.
        foreach (var cell in matrix.GetProperty("cells").EnumerateArray())
            Assert.False(cell.GetProperty("hasVerifiableLocator").GetBoolean());

        var withdrawn = await db.Set<AcademicSourceEntity>()
            .Where(s => s.WorkspaceId == workspaceId && s.ProjectId == projectId)
            .CountAsync();
        Assert.Equal(4, withdrawn);
        var excludedScreens = await db.Set<AcademicScreeningEntity>()
            .Where(s => s.WorkspaceId == workspaceId && s.Decision == "Excluded")
            .ToListAsync();
        Assert.Single(excludedScreens);
        Assert.Contains("WF_SCREEN_EXCLUDED", excludedScreens[0].ExclusionReasonsJson);
        // The retraction arrived as a stage assertion, so the evidence lane records it as
        // a lead to check rather than certifying a withdrawal it did not read from a
        // registry. The verdict therefore stays Unverified: the exclusion stands on the
        // workflow screening decision, and nothing in the row overstates what was observed.
        Assert.Equal("Unverified", excludedScreens[0].ProvenanceVerdict);

        // A completed project is stable: repeat runs change nothing.
        var again = await RunAsync(client, projectId);
        Assert.Equal("Completed", State(again));
        Assert.Equal(
            first.GetProperty("project").GetProperty("revision").GetInt64(),
            again.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal(baseline[AcademicStage.Plan] + 1, _factory.StageHandlers[AcademicStage.Plan].Calls.Count);
    }

    [Fact]
    public async Task Checkpoint_ledger_advances_monotonically_and_survives_reload()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "ledger-flow", "Ledger question");
        await RunAsync(client, projectId);
        var afterPlan = await DetailAsync(client, projectId);
        var revision = afterPlan.GetProperty("project").GetProperty("revision").GetInt64();

        var checkpoints = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        Assert.NotNull(checkpoints);
        Assert.True(checkpoints.Length >= 3);
        Assert.Equal(Enumerable.Range(0, checkpoints.Length), checkpoints.Select(c => c.GetProperty("revision").GetInt32()).ToArray());

        // Approving and resuming continues the same ledger with no gaps or reuse.
        await ApproveAsync(client, projectId, "Plan");
        var afterResume = await RunAsync(client, projectId);
        var nextRevision = afterResume.GetProperty("project").GetProperty("revision").GetInt64();
        Assert.True(nextRevision > revision);
        var later = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        Assert.Equal(0, later![0].GetProperty("revision").GetInt64());
        // The ledger is dense: revisions 0..R with no gaps, and it ends exactly where the
        // project says it does, so a reload can always be replayed from checkpoint 0.
        Assert.Equal(nextRevision, later[^1].GetProperty("revision").GetInt64());
        var keys = later.Select(c => c.GetProperty("checkpointKey").GetString()!).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Approval_replay_is_idempotent_and_stale_checkpoints_cannot_be_released()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "approval-replay", "Approval replay question");
        await RunAsync(client, projectId);

        var firstApproval = await ApproveAsync(client, projectId, "Plan");
        Assert.Equal("APPLIED", firstApproval.GetProperty("code").GetString());
        var revisionAfterApproval = firstApproval.GetProperty("project").GetProperty("revision").GetInt64();

        // Replaying the identical decision moves nothing and charges nothing.
        var replay = await ApproveAsync(client, projectId, "Plan");
        Assert.Equal("ALREADY_APPLIED", replay.GetProperty("code").GetString());
        Assert.Equal(revisionAfterApproval, replay.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Single(replay.GetProperty("approvals").EnumerateArray());

        // A decision for a checkpoint that is not pending is a conflict, never a silent pass.
        var tooEarly = await ApproveAsync(client, projectId, "Synthesis");
        Assert.Equal("NO_PENDING_APPROVAL", tooEarly.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequestRevision_invalidates_downstream_artifacts_and_reenters_the_gate()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "revision-flow", "Revision flow question");
        var baselineScreen = _factory.StageHandlers[AcademicStage.Screen].Calls.Count;
        var baselineDiscover = _factory.StageHandlers[AcademicStage.Discover].Calls.Count;
        await RunAsync(client, projectId);
        await ApproveAsync(client, projectId, "Plan");

        var toScreen = await RunAsync(client, projectId);
        Assert.Equal("AwaitingApproval", State(toScreen));
        Assert.Equal("SourceLibrary", PendingKind(toScreen));
        Assert.Equal(4, Artifact(toScreen, "sourceCount"));

        var revisionDecision = await ApproveAsync(client, projectId, "SourceLibrary",
            decision: "RequestRevision", notes: "Drop non-scholarly reports and keep to peer-reviewed work.");
        Assert.Equal("APPLIED", revisionDecision.GetProperty("code").GetString());
        // RequestRevision at the source-library gate clears the screening artifact and
        // everything downstream, but the discovery results themselves survive: the
        // reviewer asked for a stricter screen, not for a fresh search.
        Assert.Equal(4, Artifact(revisionDecision, "sourceCount"));
        Assert.Equal(0, Artifact(revisionDecision, "includedCount"));

        var rerun = await RunAsync(client, projectId);
        Assert.Equal("AwaitingApproval", State(rerun));
        Assert.Equal("SourceLibrary", PendingKind(rerun));
        Assert.Equal(4, Artifact(rerun, "sourceCount"));
        Assert.Equal(baselineScreen + 2, _factory.StageHandlers[AcademicStage.Screen].Calls.Count);
        Assert.Equal(baselineDiscover + 1, _factory.StageHandlers[AcademicStage.Discover].Calls.Count);

        // Feedback was carried into the re-run's context, so the replay is a fresh
        // fingerprint lineage rather than a cached skip.
        Assert.Contains("peer-reviewed",
            _factory.StageHandlers[AcademicStage.Screen].Calls[^1].ApprovalFeedback, StringComparison.Ordinal);

        // Budget: the ledger is append-only, so the revised screen attempt is charged on
        // top of the first one; discover ran once. Plan+Discover+Screen+Screen = 2.0 USD.
        var usage = rerun.GetProperty("usage");
        Assert.Equal(2.0m, usage.GetProperty("spentUsd").GetDecimal());
        Assert.Equal(8, usage.GetProperty("callsUsed").GetInt32());
    }

    [Fact]
    public async Task Budget_gate_pauses_a_runaway_estimate_and_a_raise_resumes_without_double_charging()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "budget-gate", "Budget gate question",
            budget: new { maxTotalUsd = 1.0, maxStageUsd = 1.0 });
        var baselinePlan = _factory.StageHandlers[AcademicStage.Plan].Calls.Count;

        // The plan estimate needs more than the ceiling allows: preflight refuses to spend.
        _factory.StageHandlers[AcademicStage.Plan].EstimateValue =
            new AcademicStageEstimate { EstimatedCostUsd = 0.9m, EstimatedProviderCalls = 2 };

        var paused = await RunAsync(client, projectId);
        Assert.Equal("PausedBudget", State(paused));
        Assert.Equal("BUDGET_STAGE_LIMIT", paused.GetProperty("project").GetProperty("stateCode").GetString());
        // Nothing was spent on the refused attempt.
        Assert.Equal(0m, paused.GetProperty("usage").GetProperty("spentUsd").GetDecimal());
        Assert.Equal(baselinePlan, _factory.StageHandlers[AcademicStage.Plan].Calls.Count);

        // Lowering is a no-op (ceilings only rise); raising to a workable ceiling unpauses.
        var lower = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/budget",
            new { maxTotalUsd = 0.1, maxStageUsd = 0.1 });
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        var lowerBody = await lower.Content.ReadFromJsonAsync<JsonElement>();
        // Ceilings only rise: an equal-after-clamping proposal is answered without a
        // checkpoint, so the project stays paused exactly as it was.
        Assert.Equal("BUDGET_UNCHANGED", lowerBody.GetProperty("code").GetString());
        Assert.Equal("PausedBudget", State(lowerBody));

        var raise = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/budget",
            new { maxTotalUsd = 12, maxStageUsd = 4 });
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        var raisedBody = await raise.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Running", State(raisedBody));

        var resumed = await RunAsync(client, projectId);
        Assert.Equal("AwaitingApproval", State(resumed));
        Assert.Equal("Plan", PendingKind(resumed));
        Assert.Equal(baselinePlan + 1, _factory.StageHandlers[AcademicStage.Plan].Calls.Count);
        var spent = resumed.GetProperty("usage").GetProperty("spentUsd").GetDecimal();
        Assert.Equal(0.5m, spent);

        // Replaying the identical raise is a no-op: same revision, no extra checkpoint.
        var beforeRevision = resumed.GetProperty("project").GetProperty("revision").GetInt64();
        var replay = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/budget",
            new { maxTotalUsd = 12, maxStageUsd = 4 });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BUDGET_UNCHANGED", replayBody.GetProperty("code").GetString());
        Assert.Equal(beforeRevision, replayBody.GetProperty("project").GetProperty("revision").GetInt64());
        var checkpoints = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        Assert.NotNull(checkpoints);
        Assert.Equal(beforeRevision + 1, checkpoints.Length);
    }

    [Fact]
    public async Task Projection_watermark_marks_stale_state_and_self_heals_on_the_next_run()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "watermark-flow", "Watermark question");
        await RunAsync(client, projectId);
        await ApproveAsync(client, projectId, "Plan");

        var withSources = await RunAsync(client, projectId);
        Assert.Equal("AwaitingApproval", State(withSources));
        Assert.True(withSources.GetProperty("evidenceCurrent").GetBoolean());

        // Roll the watermark back as a crashed projection would leave it. This is written
        // straight to the row on purpose: the store's own API refuses backward movement,
        // so the only way to reproduce the stale state is the exact DB shape a crash leaves.
        await RollBackWatermarkForTestAsync(projectId, 0);

        var stale = await DetailAsync(client, projectId);
        Assert.False(stale.GetProperty("evidenceCurrent").GetBoolean());

        // The next run re-projects from the committed checkpoint and the flag recovers.
        var healed = await RunAsync(client, projectId);
        Assert.True(healed.GetProperty("evidenceCurrent").GetBoolean());

        // Re-projection is rebuild-idempotent: identical cell count, no accumulation.
        var cellsAfterHeal = await MatrixCellCountAsync(projectId);
        Assert.True(cellsAfterHeal > 0);
        await RollBackWatermarkForTestAsync(projectId, 0);

        var healedAgain = await RunAsync(client, projectId);
        Assert.True(healedAgain.GetProperty("evidenceCurrent").GetBoolean());
        Assert.Equal(cellsAfterHeal, await MatrixCellCountAsync(projectId));
    }

    /// <summary>
    /// The combined-tree guard for mutations. A budget raise commits a newer revision
    /// without projecting it, so the tree sits honestly stale: committed r+1, watermark r.
    /// A projection call carrying the older copy of the tree must be refused outright —
    /// the previous behavior reported "already projected" (true) for a revision the
    /// durable tree had already moved past, which is exactly the certification a slow
    /// losing writer relied on while rewriting derived rows under the newer watermark.
    /// The same call with the CURRENT committed copy must project and claim the watermark,
    /// and only then may reads report evidenceCurrent again.
    /// </summary>
    [Fact]
    public async Task Stale_projection_calls_are_refused_until_the_current_revision_is_projected()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "stale-writer", "Stale projection guard question");
        await RunAsync(client, projectId);
        await ApproveAsync(client, projectId, "Plan");
        var withSources = await RunAsync(client, projectId);
        Assert.True(withSources.GetProperty("evidenceCurrent").GetBoolean());

        Guid workspace = workspaceId;
        AcademicWorkflowSnapshot stale;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AcademicProjectsService>();
            stale = (await service.LoadFencedAsync(workspace, projectId, CancellationToken.None))!;
        }

        // Commit a newer revision on the same project without projecting it.
        var raise = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/budget",
            new { maxTotalUsd = 250.0, maxTotalCalls = 1000 });
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        var raised = await raise.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BUDGET_RAISED", raised.GetProperty("code").GetString());
        var committed = raised.GetProperty("project").GetProperty("revision").GetInt64();
        Assert.Equal(stale.Revision + 1, committed);

        // The read path reports the tree as stale; nothing has been laundered.
        var detail = await DetailAsync(client, projectId);
        Assert.False(detail.GetProperty("evidenceCurrent").GetBoolean());
        var cellsBefore = await MatrixCellCountAsync(projectId);

        // A writer still holding the older copy is refused: no derived rewrite, no claim.
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AcademicProjectsService>();
            Assert.False(await service.ProjectEvidenceAsync(stale, CancellationToken.None));
        }

        Assert.False((await DetailAsync(client, projectId)).GetProperty("evidenceCurrent").GetBoolean());
        Assert.Equal(cellsBefore, await MatrixCellCountAsync(projectId));

        // The current committed copy projects and claims the watermark; the tree is
        // current again and the rebuild stayed idempotent.
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AcademicProjectsService>();
            var current = (await service.LoadFencedAsync(workspace, projectId, CancellationToken.None))!;
            Assert.Equal(committed, current.Revision);
            Assert.True(await service.ProjectEvidenceAsync(current, CancellationToken.None));
        }

        Assert.True((await DetailAsync(client, projectId)).GetProperty("evidenceCurrent").GetBoolean());
        Assert.Equal(cellsBefore, await MatrixCellCountAsync(projectId));
    }

    [Fact]
    public async Task Metadata_only_and_abstract_access_boundaries_survive_the_full_pipeline()
    {
        var (client, workspaceId) = await RegisterAsync();
        var projectId = await CreateAsync(client, workspaceId, "access-boundary", "Access boundary question");
        var result = await DrainAsync(client, projectId);
        Assert.Equal("Completed", State(result));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        // The metadata-only report was stored as metadata-only: no abstract, no full text,
        // and its bibliographic support kind reflects unread content.
        var metadataOnly = await db.Set<AcademicEvidenceItemEntity>()
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.AccessLevelAtRecord == "MetadataOnly")
            .ToListAsync();
        Assert.All(metadataOnly, item => Assert.NotEqual("VerifiedAgainstSource", item.Integrity));

        // The abstract-verbatim quote was matched against stored abstract text and earned
        // VerifiedAgainstSource; the locator that came with it was rejected as unanchored,
        // so it can never render as a page pinpoint.
        var anchored = await db.Set<AcademicEvidenceItemEntity>()
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.LocatorRejectionCode == "LOCATOR_MEMBER_ASSERTED")
            .ToListAsync();
        Assert.NotEmpty(anchored);
        // The workflow lane's attribution is stored alongside the pipeline code, so either
        // lane reading the row learns where the unanchored pinpoint came from.
        Assert.All(anchored, item => Assert.Contains("WF_LOCATOR_UNANCHORED", item.IntegrityReasonsJson));

        // No persisted matrix cell anywhere claims a verifiable locator.
        Assert.Equal(0, await db.Set<AcademicMatrixCellEntity>()
            .CountAsync(c => c.WorkspaceId == workspaceId && c.HasVerifiableLocator));
    }

    // --- helpers ---------------------------------------------------------------------------------

    private async Task<JsonElement> DrainAsync(HttpClient client, Guid projectId)
    {
        var result = await RunAsync(client, projectId);
        for (var i = 0; i < 32 && State(result) is "Running" or "AwaitingApproval"; i++)
        {
            if (PendingKind(result) is string kind) await ApproveAsync(client, projectId, kind);
            result = await RunAsync(client, projectId);
        }

        return result;
    }

    private async Task RollBackWatermarkForTestAsync(Guid projectId, long to)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var row = await db.Set<AcademicWorkflowProjectEntity>().FirstAsync(e => e.ProjectId == projectId);
        row.EvidenceProjectedRevision = to;
        await db.SaveChangesAsync();
    }

    private async Task<int> MatrixCellCountAsync(Guid projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.Set<AcademicMatrixCellEntity>().CountAsync(c => c.ProjectId == projectId);
    }


    private async Task<(HttpClient Client, Guid WorkspaceId)> RegisterAsync()
    {
        var client = _factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    displayName = "Connected Flow Tester",
                    email = $"academic-connected-{Guid.NewGuid():N}@example.com",
                    password = "StrongPassword!123",
                    preferredLanguage = "en",
                }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        return (client, auth.PersonalWorkspace.Id);
    }


    private async Task<Guid> CreateAsync(
        HttpClient client,
        Guid workspaceId,
        string requestKey,
        string question,
        object? budget = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["requestKey"] = requestKey + "-" + Guid.NewGuid().ToString("N")[..8],
            ["researchQuestion"] = question,
            ["discipline"] = "Health Economics",
        };
        if (budget is not null) payload["budget"] = budget;
        var response = await PostJsonAsync(client, $"/api/workspaces/{workspaceId}/academic-projects", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();
    }

    private async Task<JsonElement> RunAsync(HttpClient client, Guid projectId)
    {
        var response = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> DetailAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/academic-projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ApproveAsync(
        HttpClient client,
        Guid projectId,
        string kind,
        string decision = "Approved",
        string? notes = null)
    {
        var response = await PostJsonAsync(client,
            $"/api/academic-projects/{projectId}/approvals/{kind}",
            new { decision, notes });
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static string State(JsonElement dto) => dto.GetProperty("project").GetProperty("state").GetString()!;

    private static string? PendingKind(JsonElement dto) =>
        dto.GetProperty("pendingApproval").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : dto.GetProperty("pendingApproval").GetProperty("kind").GetString();

    private static int Artifact(JsonElement dto, string property) => dto.GetProperty(property).GetInt32();
}

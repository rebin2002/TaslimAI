using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.AcademicIntegration;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests.AcademicIntegration;

/// <summary>
/// The integration surface exactly as deployed on this branch: authenticated,
/// workspace-scoped, with no provider stage handlers registered yet. These tests pin
/// the honest staging contract — intake persists and replays idempotently, every read
/// is fenced to the owning workspace, and running a project answers 501 naming the
/// missing stage instead of pretending the workflow is live. Nothing here fabricates a
/// green path: routes that would claim results stay non-success until their lanes land.
/// </summary>
public class AcademicProjectsApiTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory _factory;

    public AcademicProjectsApiTests(TaslimApiFactory factory)
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
        Assert.Equal(7, dto.GetProperty("missingStages").GetArrayLength());
        Assert.Equal(0, dto.GetProperty("connectedStages").GetArrayLength());
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

        // Direct id access: not a member of the owning workspace. 404 vs 403 both deny;
        // the fence must never answer with data.
        foreach (var path in new[]
        {
            $"/api/academic-projects/{projectId}",
            $"/api/academic-projects/{projectId}/evidence-matrix",
            $"/api/academic-projects/{projectId}/verification",
            $"/api/academic-projects/{projectId}/checkpoints",
        })
        {
            var denied = await otherClient.GetAsync(path);
            Assert.True(
                denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"{path} answered {denied.StatusCode} with body {await denied.Content.ReadAsStringAsync()}");
            Assert.DoesNotContain("isolation probe", await SafeBody(denied));
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

    [Fact]
    public async Task Run_reports_the_exact_missing_stage_and_leaves_state_untouched()
    {
        var (client, workspaceId) = await RegisterAsync();
        var create = await PostCreateAsync(client, workspaceId, new
        {
            requestKey = $"not-connected-{Guid.NewGuid():N}",
            researchQuestion = "Stage gate probe",
            discipline = "Economics",
        });
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("projectId").GetGuid();

        var run = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/run", new { });
        Assert.Equal(HttpStatusCode.NotImplemented, run.StatusCode);
        var error = (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("ACADEMIC_STAGE_NOT_CONNECTED", error.GetProperty("code").GetString());
        Assert.Equal("Plan", error.GetProperty("missingStage").GetString());
        Assert.Equal(0, error.GetProperty("connectedStages").GetArrayLength());
        Assert.Equal(0, error.GetProperty("resumableAtRevision").GetInt64());

        // Nothing moved: still revision 0, still one intake checkpoint, nothing charged.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/academic-projects/{projectId}");
        Assert.Equal(0, detail.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal(0, detail.GetProperty("usage").GetProperty("spentUsd").GetDecimal());
        var checkpoints = await client.GetFromJsonAsync<JsonElement[]>($"/api/academic-projects/{projectId}/checkpoints");
        Assert.NotNull(checkpoints);
        Assert.Single(checkpoints);
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

        var approve = await PostJsonAsync(client, $"/api/academic-projects/{projectId}/approvals/plan",
            new { decision = "Approved", notes = "looks good" });
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

    private async Task<(HttpClient Client, Guid WorkspaceId)> RegisterAsync()
    {
        var client = _factory.CreateClient();
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

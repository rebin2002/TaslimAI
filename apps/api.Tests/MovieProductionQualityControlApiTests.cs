using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionQualityControlApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public MovieProductionQualityControlApiTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Preview_accepts_measured_output_without_persisting_or_queueing_work()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await CreateProject(client, auth.PersonalWorkspace.Id, "QC acceptance");

        using var response = await SendWithCsrf(client, new Uri($"/api/movie-studio/projects/{project.Project.Id}/production-qc/preview", UriKind.Relative), ValidRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(MovieProductionQualityControlService.ContractVersion, root.GetProperty("contractVersion").GetString());
        Assert.Equal(MovieProductionQcActions.Accept, root.GetProperty("action").GetString());
        Assert.False(root.GetProperty("requiresHumanReview").GetBoolean());
        Assert.Contains(root.GetProperty("findings").EnumerateArray(), item => item.GetProperty("reasonCode").GetString() == MovieProductionQcReasonCodes.Accepted);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(0, await db.MovieProductionVersions.CountAsync(item => item.MovieShot.Scene.MovieProjectId == project.Project.Id));
    }

    [Fact]
    public async Task Preview_returns_bounded_reason_codes_when_evidence_requires_review()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var project = await CreateProject(client, auth.PersonalWorkspace.Id, "QC review");
        var request = new
        {
            requirements = new
            {
                targetResolution = new { width = 1920, height = 1080 },
                minimumSourceResolution = new { width = 1280, height = 720 },
                expectedDurationSeconds = 24,
                requiredContinuitySnapshotHash = "continuity-v1",
            },
            evidence = new
            {
                durationSeconds = 30,
                continuitySnapshotHash = "wrong-hash",
                hardContinuityConflictCount = 1,
            },
        };

        using var response = await SendWithCsrf(client, new Uri($"/api/movie-studio/projects/{project.Project.Id}/production-qc/preview", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var findings = body.RootElement.GetProperty("findings").EnumerateArray().Select(item => item.GetProperty("reasonCode").GetString()).ToArray();
        Assert.Equal(MovieProductionQcActions.RequireReview, body.RootElement.GetProperty("action").GetString());
        Assert.True(body.RootElement.GetProperty("requiresHumanReview").GetBoolean());
        Assert.Contains(MovieProductionQcReasonCodes.MeasurementMissing, findings);
        Assert.Contains(MovieProductionQcReasonCodes.DurationOutOfTolerance, findings);
        Assert.Contains(MovieProductionQcReasonCodes.ContinuityHashMismatch, findings);
        Assert.Contains(MovieProductionQcReasonCodes.HardContinuityConflict, findings);
    }

    [Fact]
    public async Task Preview_does_not_disclose_a_project_to_an_unauthorized_user()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "QC isolation");

        using var outsider = factory.CreateClient();
        await Register(outsider);
        using var response = await SendWithCsrf(outsider, new Uri($"/api/movie-studio/projects/{project.Project.Id}/production-qc/preview", UriKind.Relative), ValidRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object ValidRequest() => new
    {
        requirements = new
        {
            targetResolution = new { width = 1920, height = 1080 },
            minimumSourceResolution = new { width = 1280, height = 720 },
            expectedDurationSeconds = 24,
            requiredContinuitySnapshotHash = "continuity-v1",
        },
        evidence = new
        {
            currentResolution = new { width = 1920, height = 1080 },
            durationSeconds = 24,
            continuitySnapshotHash = "continuity-v1",
            continuityWarningCount = 0,
            hardContinuityConflictCount = 0,
            outputSha256 = "bounded-test-output-hash",
        },
    };

    private async Task<AuthResponse> Register(HttpClient client)
    {
        using var response = await SendWithCsrf(client, new Uri("/api/auth/register", UriKind.Relative), new
        {
            displayName = "QC API Tester",
            email = $"movie-qc-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateProject(HttpClient client, Guid workspaceId, string title)
    {
        using var response = await SendWithCsrf(client, new Uri("/api/movie-studio/projects", UriKind.Relative), new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title,
            description = "A provider-neutral QC preview test.",
            durationSeconds = 24,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, Uri uri, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

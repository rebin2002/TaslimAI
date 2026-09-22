using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ResearchGeneration:Enabled", "true");
        builder.UseSetting("ResearchGeneration:MaxSourceCount", "8");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IResearchSearchProvider>();
            services.RemoveAll<IResearchReportProvider>();
            services.AddSingleton<IResearchSearchProvider, FakeResearchSearchProvider>();
            services.AddSingleton<IResearchReportProvider, FakeResearchReportProvider>();
        });
    }
}

public sealed class ResearchGenerationTests : IClassFixture<ResearchApiFactory>
{
    private readonly ResearchApiFactory factory;

    public ResearchGenerationTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(100).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        Task.Delay(100).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Research_job_creates_cited_report_asset_representations_sources_and_zero_charge_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-{Guid.NewGuid():N}@example.com");
        var createdResponse = await SendWithCsrf<CreateResearchGenerationResponse>(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "What are the current opportunities and risks for solar energy in Iraq?",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var completed = await WaitForTerminal(client, createdResponse.Job.Id);
        Assert.True(completed.Status == "Succeeded", $"Status={completed.Status}; Code={completed.ErrorCode}; Message={completed.ErrorMessage}");
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("S1", completed.ResultJson, StringComparison.Ordinal);
        Assert.Equal(2, completed.Outputs.Count);

        var sourceResponse = await client.GetFromJsonAsync<JsonElement>($"/api/research-generation/jobs/{createdResponse.Job.Id}/sources");
        Assert.Equal(2, sourceResponse.GetProperty("sources").GetArrayLength());
        Assert.Equal("S1", sourceResponse.GetProperty("sources")[0].GetProperty("citationId").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == createdResponse.Job.Id);
        Assert.Equal(UsageFeature.Research, usage.Feature);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Contains("search", usage.SafeMetadataJson, StringComparison.Ordinal);
        var asset = await db.Assets.AsNoTracking().Include(item => item.Representations).SingleAsync(item => item.SourceGenerationJobId == createdResponse.Job.Id);
        Assert.Equal(AssetTypes.Research, asset.AssetType);
        Assert.Equal(new[] { AssetRepresentationTypes.Docx, AssetRepresentationTypes.Pdf }, asset.Representations.OrderBy(item => item.RepresentationType).Select(item => item.RepresentationType));
        Assert.Equal(2, await db.ResearchEvidence.CountAsync(item => item.GenerationJobId == createdResponse.Job.Id));
    }

    [Fact]
    public async Task Research_creation_is_workspace_authorized_and_requires_a_source_when_web_is_disabled()
    {
        using var first = factory.CreateClient();
        var firstAuth = await Register(first, $"research-first-{Guid.NewGuid():N}@example.com");
        using var second = factory.CreateClient();
        var secondAuth = await Register(second, $"research-second-{Guid.NewGuid():N}@example.com");
        var crossWorkspace = await SendWithCsrf(second, HttpMethod.Post, "/api/research-generation/jobs", new { workspaceId = firstAuth.PersonalWorkspace.Id, question = "A valid question", useWebSources = true, attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Forbidden, crossWorkspace.StatusCode);
        var noSource = await SendWithCsrf(first, HttpMethod.Post, "/api/research-generation/jobs", new { workspaceId = firstAuth.PersonalWorkspace.Id, question = "A valid question", useWebSources = false, attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        var error = await noSource.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ResearchSourceUnavailable, error.GetProperty("error").GetProperty("code").GetString());
        _ = secondAuth;
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Research Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            using var response = await client.GetAsync($"/api/generation/jobs/{id}");
            if (response.IsSuccessStatusCode)
            {
                var current = await response.Content.ReadFromJsonAsync<GenerationJobDto>();
                if (current is not null && current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Research job did not reach a terminal state.");
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await SendWithCsrf(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var dto = await response.Content.ReadFromJsonAsync<T>();
        return dto!;
    }
}

public sealed class FakeResearchSearchProvider : IResearchSearchProvider
{
    public Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/energy", "https://example.gov/energy", "Energy ministry report", "example.gov", "Example Ministry", null, DateTime.UtcNow, "web", "Official energy evidence.", "Official energy evidence.", request.Plan.SearchQueries[0], 1, true, null),
            new ResearchSourceCandidate("S2", "https://example.org/market", "https://example.org/market", "Market outlook", "example.org", "Example Institute", null, DateTime.UtcNow, "web", "Independent market evidence.", "Independent market evidence.", request.Plan.SearchQueries[0], 2, true, null),
        };
        var evidence = sources.Select(source => new ResearchEvidenceCandidate(source.CitationId, "market evidence", source.ExtractedText!, null, null)).ToArray();
        var usage = new AiUsageMetadata("test", "research-search-test", 100, null, 50, 0m, 0m, 5, "completed", true, SafeMetadataJson: "{\"researchStage\":\"search\"}");
        return Task.FromResult(new ResearchSearchResult(sources, evidence, usage));
    }
}

public sealed class FakeResearchReportProvider : IResearchReportProvider
{
    public Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = new ResearchDraft
        {
            Title = "Solar energy research",
            Subtitle = "Cited overview",
            Language = "en",
            ExecutiveSummary = "The evidence indicates a developing opportunity with material execution risks.",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Official evidence identifies the opportunity. [S1]", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Evidence and risks", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Independent market evidence adds context. [S2]", CitationIds = ["S2"] }] }],
            Conclusion = "Review the source evidence before making a decision.",
            Sources = ["S1", "S2"],
        };
        var usage = new AiUsageMetadata("test", "research-report-test", 200, null, 150, 0m, 0m, 10, "completed", true, SafeMetadataJson: "{\"researchStage\":\"report\"}");
        return Task.FromResult(new ResearchReportProviderResult(draft, usage));
    }
}
